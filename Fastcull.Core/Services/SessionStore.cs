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
    /// <summary>
    /// A pending write for one photo. Each field is optional so a write can say "set the rating,
    /// leave rotation alone" or the reverse.
    ///
    /// That optionality is load-bearing rather than tidiness. The background writer collapses
    /// pending writes by path so rapid changes to one photo land as one row - which means a
    /// rating write and a rotation write for the same photo inside one batch would otherwise
    /// overwrite each other, and whichever arrived second would silently reset the other's value.
    /// Unset fields are merged, then written with COALESCE so they keep whatever is in the row.
    /// </summary>
    public readonly record struct PhotoWrite(string Path, Flag? Flag, int? Stars, int? RotationQuarterTurns)
    {
        /// <summary>Later values win field by field; fields the later write left unset survive.</summary>
        public PhotoWrite MergedWith(PhotoWrite later) => new(
            Path,
            later.Flag ?? Flag,
            later.Stars ?? Stars,
            later.RotationQuarterTurns ?? RotationQuarterTurns);
    }

    /// <summary>
    /// Everything persisted about one photo. A single record per photo rather than one dictionary
    /// per field: two dictionaries keyed by path that have to be kept in sync is a bug waiting to
    /// happen.
    /// </summary>
    public readonly record struct StoredPhotoState(CullState Cull, Rotation Rotation)
    {
        public static readonly StoredPhotoState Default = new(CullState.Default, Rotation.None);
    }

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
        private readonly Channel<PhotoWrite> _channel;
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
            _channel = Channel.CreateBounded<PhotoWrite>(new BoundedChannelOptions(4096)
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
                  rotation       INTEGER NOT NULL DEFAULT 0,
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

            await MigrateAsync(connection).ConfigureAwait(false);

            cmd.CommandText = "SELECT COUNT(*) FROM session_meta;";
            var count = System.Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
            if (count == 0)
            {
                cmd.CommandText = "INSERT INTO session_meta (root_path) VALUES ($root);";
                cmd.Parameters.AddWithValue("$root", rootFolder);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Schema revision. 1 added photos.rotation (PRD 1.11).</summary>
        public const int SchemaVersion = 1;

        /// <summary>
        /// Brings an existing database up to <see cref="SchemaVersion"/>.
        ///
        /// This exists because CREATE TABLE IF NOT EXISTS is a no-op against a database that
        /// already has the table - so adding a column to that statement does nothing whatsoever
        /// for the session databases already sitting in %LOCALAPPDATA%, and the first rotation
        /// write against one would fail with "no such column: rotation".
        ///
        /// The guard is PRAGMA table_info rather than user_version alone, deliberately. A fresh
        /// database gets the column from CREATE TABLE while its user_version is still 0, so a
        /// version-only check would try to add a column that already exists and throw. Asking
        /// the schema what it actually contains is correct for both paths and is idempotent.
        /// user_version is still recorded, for cheap versioning of whatever comes next.
        /// </summary>
        private static async Task MigrateAsync(SqliteConnection connection)
        {
            if (!await ColumnExistsAsync(connection, "photos", "rotation").ConfigureAwait(false))
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE photos ADD COLUMN rotation INTEGER NOT NULL DEFAULT 0;";
                await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using var version = connection.CreateCommand();
            version.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            await version.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string table, string column)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table});";

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                // table_info columns: cid, name, type, notnull, dflt_value, pk
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Current schema revision recorded in the database.</summary>
        public async Task<int> ReadSchemaVersionAsync()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return System.Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false));
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
            // (flag, stars) pair cannot reach the database through this path. Rotation is left
            // unset so this write cannot disturb it.
            _channel.Writer.TryWrite(new PhotoWrite(path, state.Flag, state.Stars, null));
        }

        /// <summary>
        /// Fire-and-forget rotation write (PRD 1.11). Same guarantees as <see cref="QueueRating"/>:
        /// synchronous, non-blocking, never throws, safe on the UI thread every keypress. Flag and
        /// stars are left unset so rotating a photo can never disturb its rating.
        /// </summary>
        public void QueueRotation(string path, Rotation rotation)
        {
            _channel.Writer.TryWrite(new PhotoWrite(path, null, null, rotation.QuarterTurns));
        }

        private async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            // Collapse repeated writes to the same photo: rapid laddering on one image should
            // land one row, not forty (work order H.4).
            var pending = new Dictionary<string, PhotoWrite>(StringComparer.OrdinalIgnoreCase);
            var reader = _channel.Reader;

            // Merge rather than replace: a rating write and a rotation write for the same photo
            // in one batch must combine, not overwrite one another.
            void Accumulate(PhotoWrite write) =>
                pending[write.Path] = pending.TryGetValue(write.Path, out var existing)
                    ? existing.MergedWith(write)
                    : write;

            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var deadline = DateTime.UtcNow + BatchInterval;

                    while (true)
                    {
                        while (reader.TryRead(out var write))
                        {
                            Accumulate(write);
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

            while (reader.TryRead(out var late)) Accumulate(late);
            if (pending.Count > 0)
            {
                try { await FlushAsync(pending).ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"[FastCull] SessionStore final flush failed: {ex}"); }
            }
        }

        private async Task FlushAsync(Dictionary<string, PhotoWrite> batch)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;

            // COALESCE so an unset field keeps whatever the row already holds. This is what lets
            // a rotation write leave the rating alone, and vice versa, without the writer having
            // to read the row first.
            cmd.CommandText = """
                UPDATE photos SET
                  flag     = COALESCE($flag, flag),
                  stars    = COALESCE($stars, stars),
                  rotation = COALESCE($rotation, rotation)
                WHERE path = $path;
                """;

            var pFlag = cmd.Parameters.Add("$flag", SqliteType.Integer);
            var pStars = cmd.Parameters.Add("$stars", SqliteType.Integer);
            var pRotation = cmd.Parameters.Add("$rotation", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);

            foreach (var write in batch.Values)
            {
                pFlag.Value = write.Flag is Flag f ? (int)f : (object)DBNull.Value;
                pStars.Value = (object?)write.Stars ?? DBNull.Value;
                pRotation.Value = (object?)write.RotationQuarterTurns ?? DBNull.Value;
                pPath.Value = write.Path;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Loads everything persisted per photo, keyed by path. Photos with no stored row are
        /// simply absent, and the caller gives them <see cref="StoredPhotoState.Default"/>.
        /// </summary>
        public async Task<Dictionary<string, StoredPhotoState>> LoadPhotoStatesAsync()
        {
            var result = new Dictionary<string, StoredPhotoState>(StringComparer.OrdinalIgnoreCase);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, flag, stars, rotation FROM photos;";
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                var flag = (Flag)reader.GetInt32(1);
                var stars = reader.GetInt32(2);

                // FromQuarterTurns normalises, so even a hand-edited 47 or -3 lands in 0-3
                // rather than rendering at an arbitrary angle.
                var rotation = Rotation.FromQuarterTurns(reader.IsDBNull(3) ? 0 : reader.GetInt32(3));

                try
                {
                    result[path] = new StoredPhotoState(new CullState(flag, stars), rotation);
                }
                catch (Exception)
                {
                    // A row that violates the ladder invariants (hand-edited, or written by an
                    // older build) must not stop the session loading - fall back to Default, but
                    // keep the rotation, which has no invariant to violate.
                    result[path] = new StoredPhotoState(CullState.Default, rotation);
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
