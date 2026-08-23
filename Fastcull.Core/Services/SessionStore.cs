using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Fastcull.Models;
using Microsoft.Data.Sqlite;

namespace Fastcull.Services
{
    /// <summary>A pending rating write for one photo.</summary>
    public readonly record struct RatingWrite(string Path, Flag Flag, int Stars);

    /// <summary>
    /// File-backed session persistence, per PRD 3.1.
    ///
    /// Every write goes through a single background writer draining a bounded channel, so the
    /// UI thread never awaits the database - CLAUDE.md's non-negotiable constraint, not a
    /// preference. <see cref="QueueRating"/> is synchronous, non-blocking and safe to call from
    /// the UI thread. Batches flush on a 250 ms timer or 32 pending operations, whichever comes
    /// first.
    /// </summary>
    public sealed class SessionStore : IAsyncDisposable
    {
        public const int BatchSize = 32;
        public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

        private readonly SqliteConnection _connection;
        private readonly Channel<RatingWrite> _channel;
        private readonly Task _writerTask;
        private readonly CancellationTokenSource _shutdown = new();

        public string DatabasePath { get; }
        public string RootFolder { get; }

        private SessionStore(SqliteConnection connection, string databasePath, string rootFolder)
        {
            _connection = connection;
            DatabasePath = databasePath;
            RootFolder = rootFolder;

            // Bounded so a runaway producer cannot grow without limit; the UI never awaits it
            // because QueueRating uses TryWrite rather than WriteAsync.
            _channel = Channel.CreateBounded<RatingWrite>(new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

            _writerTask = Task.Run(() => RunWriterAsync(_shutdown.Token));
        }

        /// <summary>Default location from PRD 3.1: %LOCALAPPDATA%\FastCull\sessions\{guid}.db.</summary>
        public static string DefaultSessionDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastCull", "sessions");

        /// <summary>
        /// Opens the session database for a scan root. If a session for that same root already
        /// exists it is reused, so ratings survive a restart (PRD 3.1 / work order H.3).
        /// </summary>
        public static async Task<SessionStore> OpenAsync(string rootFolder, string? sessionDirectory = null)
        {
            sessionDirectory ??= DefaultSessionDirectory;
            Directory.CreateDirectory(sessionDirectory);

            var existing = FindSessionForRoot(sessionDirectory, rootFolder);
            var databasePath = existing ?? Path.Combine(sessionDirectory, $"{Guid.NewGuid()}.db");

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());

            await connection.OpenAsync().ConfigureAwait(false);
            await InitialiseSchemaAsync(connection, rootFolder).ConfigureAwait(false);

            return new SessionStore(connection, databasePath, rootFolder);
        }

        private static string? FindSessionForRoot(string sessionDirectory, string rootFolder)
        {
            foreach (var candidate in Directory.EnumerateFiles(sessionDirectory, "*.db"))
            {
                try
                {
                    using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = candidate,
                        Mode = SqliteOpenMode.ReadOnly,
                    }.ToString());
                    probe.Open();

                    using var cmd = probe.CreateCommand();
                    cmd.CommandText = "SELECT root_path FROM session_meta LIMIT 1;";
                    var stored = cmd.ExecuteScalar() as string;

                    if (string.Equals(stored, rootFolder, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
                catch (Exception)
                {
                    // A corrupt or foreign .db in the folder must never stop the app opening.
                }
            }

            return null;
        }

        private static async Task InitialiseSchemaAsync(SqliteConnection connection, string rootFolder)
        {
            using var cmd = connection.CreateCommand();

            // PRD 3.1: WAL + synchronous=NORMAL.
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            // photos/companions are verbatim from PRD 3.1. session_meta is an addition - the PRD
            // schema has nowhere to record which folder a session belongs to, which H.3 requires
            // in order to match a session to a scan root on relaunch.
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS photos (
                  id             INTEGER PRIMARY KEY,
                  path           TEXT NOT NULL UNIQUE,
                  rel_path       TEXT NOT NULL,
                  basename       TEXT NOT NULL,
                  extension      TEXT NOT NULL,
                  format_family  INTEGER NOT NULL,
                  sort_time      TEXT NOT NULL,
                  sort_time_tier INTEGER NOT NULL,
                  capture_subsec INTEGER,
                  file_bytes     INTEGER NOT NULL,
                  flag           INTEGER NOT NULL DEFAULT 0,
                  stars          INTEGER NOT NULL DEFAULT 0,
                  deleted        INTEGER NOT NULL DEFAULT 0,
                  image_w        INTEGER,
                  image_h        INTEGER,
                  preview_w      INTEGER,
                  preview_h      INTEGER,
                  thumb_blob     BLOB,
                  meta_json      TEXT
                );
                CREATE TABLE IF NOT EXISTS companions (photo_id INTEGER, path TEXT, kind TEXT);
                CREATE INDEX IF NOT EXISTS idx_sort ON photos(sort_time, capture_subsec, path);
                CREATE TABLE IF NOT EXISTS session_meta (root_path TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            cmd.CommandText = "SELECT COUNT(*) FROM session_meta;";
            var count = System.Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
            if (count == 0)
            {
                cmd.CommandText = "INSERT INTO session_meta (root_path) VALUES ($root);";
                cmd.Parameters.AddWithValue("$root", rootFolder);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Registers the scanned set so every photo has a row. Runs once after the scan; unlike
        /// rating writes this is awaited, because nothing can be rated until it completes.
        /// </summary>
        public async Task RegisterPhotosAsync(IEnumerable<ScannedPhoto> photos)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO photos (path, rel_path, basename, extension, format_family,
                                    sort_time, sort_time_tier, capture_subsec, file_bytes)
                VALUES ($path, $rel, $base, $ext, $family, $sort, $tier, $subsec, $bytes)
                ON CONFLICT(path) DO NOTHING;
                """;

            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pRel = cmd.Parameters.Add("$rel", SqliteType.Text);
            var pBase = cmd.Parameters.Add("$base", SqliteType.Text);
            var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
            var pFamily = cmd.Parameters.Add("$family", SqliteType.Integer);
            var pSort = cmd.Parameters.Add("$sort", SqliteType.Text);
            var pTier = cmd.Parameters.Add("$tier", SqliteType.Integer);
            var pSubsec = cmd.Parameters.Add("$subsec", SqliteType.Integer);
            var pBytes = cmd.Parameters.Add("$bytes", SqliteType.Integer);

            foreach (var photo in photos)
            {
                pPath.Value = photo.FilePath;
                pRel.Value = photo.RelativePath;
                pBase.Value = Path.GetFileNameWithoutExtension(photo.FileName);
                pExt.Value = Path.GetExtension(photo.FileName);
                pFamily.Value = (int)photo.Family;
                pSort.Value = photo.SortTime.ToString("O");
                pTier.Value = (int)photo.SortTimeSource;
                pSubsec.Value = (object?)photo.CaptureSubsec ?? DBNull.Value;
                pBytes.Value = photo.FileBytes;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Fire-and-forget rating write. Synchronous, non-blocking, never throws - safe to call
        /// from the UI thread on every keypress (PRD 1.6: the write never gates the visual).
        /// </summary>
        public void QueueRating(string path, CullState state)
        {
            // Constructing through CullState already guarantees the invariants, so an invalid
            // (flag, stars) pair cannot reach the database through this path.
            _channel.Writer.TryWrite(new RatingWrite(path, state.Flag, state.Stars));
        }

        private async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            // Collapse repeated writes to the same photo: rapid laddering on one image should
            // land one row, not forty (work order H.4).
            var pending = new Dictionary<string, RatingWrite>(StringComparer.OrdinalIgnoreCase);
            var reader = _channel.Reader;

            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var deadline = DateTime.UtcNow + BatchInterval;

                    while (true)
                    {
                        while (reader.TryRead(out var write))
                        {
                            pending[write.Path] = write;
                            if (pending.Count >= BatchSize) break;
                        }

                        if (pending.Count >= BatchSize || DateTime.UtcNow >= deadline) break;

                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;

                        try
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            linked.CancelAfter(remaining);
                            if (!await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) break;
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            break;   // batch interval elapsed
                        }
                    }

                    if (pending.Count > 0)
                    {
                        await FlushAsync(pending).ConfigureAwait(false);
                        pending.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down - drain whatever is left so a rating made just before close is
                // not lost (work order H.2).
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FastCull] SessionStore writer failed: {ex}");
            }

            while (reader.TryRead(out var late)) pending[late.Path] = late;
            if (pending.Count > 0)
            {
                try { await FlushAsync(pending).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[FastCull] SessionStore final flush failed: {ex}"); }
            }
        }

        private async Task FlushAsync(Dictionary<string, RatingWrite> batch)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE photos SET flag = $flag, stars = $stars WHERE path = $path;";

            var pFlag = cmd.Parameters.Add("$flag", SqliteType.Integer);
            var pStars = cmd.Parameters.Add("$stars", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);

            foreach (var write in batch.Values)
            {
                pFlag.Value = (int)write.Flag;
                pStars.Value = write.Stars;
                pPath.Value = write.Path;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Loads stored ratings keyed by path. Photos with no stored row are simply absent, and
        /// the caller gives them <see cref="CullState.Default"/>.
        /// </summary>
        public async Task<Dictionary<string, CullState>> LoadRatingsAsync()
        {
            var result = new Dictionary<string, CullState>(StringComparer.OrdinalIgnoreCase);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, flag, stars FROM photos;";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                var flag = (Flag)reader.GetInt32(1);
                var stars = reader.GetInt32(2);

                try
                {
                    result[path] = new CullState(flag, stars);
                }
                catch (Exception)
                {
                    // A row that violates the ladder invariants (hand-edited, or written by an
                    // older build) must not stop the session loading - fall back to Default.
                    result[path] = CullState.Default;
                }
            }

            return result;
        }

        /// <summary>Drains and flushes pending writes, then closes the database.</summary>
        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();

            try { await _writerTask.ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[FastCull] SessionStore shutdown: {ex}"); }

            _shutdown.Cancel();
            _shutdown.Dispose();

            _connection.Close();
            _connection.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }
}
