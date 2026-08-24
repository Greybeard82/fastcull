using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Fastcull.Services
{
    /// <summary>
    /// Every filesystem primitive the finish engine touches, behind one seam.
    ///
    /// **This interface exists for testability, and that is not a luxury here.** The engine's
    /// interesting behaviour is almost entirely on its failure paths - a verify mismatch, a denied
    /// delete, a disk that fills mid-run - and those are the paths that decide whether somebody's
    /// photographs survive. Trying to provoke them against a real disk is unreliable and
    /// unrepeatable; injecting them is neither. Real runs get
    /// <see cref="SystemFinishFileSystem"/> and nothing is abstracted away from them.
    /// </summary>
    public interface IFinishFileSystem
    {
        bool FileExists(string path);
        void CreateDirectory(string path);
        long GetFileLength(string path);
        DateTime GetLastWriteTimeUtc(string path);
        void SetLastWriteTimeUtc(string path, DateTime utc);

        /// <summary>
        /// Streams source to destination and returns the hash of the bytes **as they were read**.
        ///
        /// Hashing during the copy rather than afterwards is what keeps verification to one extra
        /// pass: the source has to be read anyway, so its digest is free, and only the destination
        /// needs re-reading. Must throw rather than half-succeed; the caller deletes the partial
        /// destination on any throw.
        /// </summary>
        Task<byte[]> CopyAsync(string source, string destination, CancellationToken cancellationToken);

        Task<byte[]> HashAsync(string path, CancellationToken cancellationToken);

        void DeleteFile(string path);

        /// <summary>Free bytes on the volume containing <paramref name="path"/>, or null if unknown.</summary>
        long? GetAvailableFreeSpace(string path);

        void WriteAllText(string path, string contents);
    }

    /// <summary>The real thing. No behaviour lives here that is not a direct call to System.IO.</summary>
    public sealed class SystemFinishFileSystem : IFinishFileSystem
    {
        /// <summary>
        /// 1 MB. Large enough that a 25 MB RAW is a couple of dozen reads rather than hundreds,
        /// small enough that cancellation is observed promptly - the token is checked once per
        /// buffer, so this also sets how long a cancel can take to be noticed mid-file.
        /// </summary>
        private const int BufferBytes = 1024 * 1024;

        public bool FileExists(string path) => File.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public long GetFileLength(string path) => new FileInfo(path).Length;

        public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

        public async Task<byte[]> CopyAsync(string source, string destination, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // FileMode.CreateNew, deliberately: the engine promises never to overwrite, and this
            // makes the filesystem itself enforce it. If a destination appeared between the
            // collision check and here, this throws rather than destroying it.
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                                             BufferBytes, useAsync: true);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                              BufferBytes, useAsync: true);

            var buffer = new byte[BufferBytes];
            int read;

            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }

            // Flush through to disk before anyone is told the copy succeeded. Without this the
            // verify could read back from the OS cache and pass on bytes that never reached the
            // platter - which on a Move would then authorise deleting the original.
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);

            return hash.GetHashAndReset();
        }

        public async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                             BufferBytes, useAsync: true);

            var buffer = new byte[BufferBytes];
            int read;

            while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);

            return hash.GetHashAndReset();
        }

        public void DeleteFile(string path) => File.Delete(path);

        public long? GetAvailableFreeSpace(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return null;

                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                // A UNC path or an unusual volume can throw here. "Unknown" is honest; the engine
                // treats it as "cannot check" rather than as "no space".
                return null;
            }
        }

        public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    }
}
