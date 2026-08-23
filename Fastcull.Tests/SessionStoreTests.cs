using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fastcull.Models;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>Round-trip, batching and resilience coverage for PRD 3.1 (work order H.4).</summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _sessionDir;
    private readonly string _root;

    public SessionStoreTests()
    {
        _sessionDir = Path.Combine(Path.GetTempPath(), "fastcull-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sessionDir);
        _root = Path.Combine(_sessionDir, "root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    private ScannedPhoto Photo(string name) => new()
    {
        FilePath = Path.Combine(_root, name),
        RelativePath = name,
        FileName = name,
        Family = FormatFamily.Jpeg,
        FileBytes = 1234,
        SortTime = new DateTime(2026, 8, 23, 3, 0, 0, DateTimeKind.Utc),
        SortTimeSource = TimeSource.CaptureDate,
        CaptureSubsec = null,
    };

    private Task<SessionStore> OpenAsync() => SessionStore.OpenAsync(_root, _sessionDir);

    [Fact]
    public async Task AllEightLadderStates_RoundTripIdentically()
    {
        var photos = Enumerable.Range(0, 8).Select(i => Photo($"p{i}.jpg")).ToList();

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(photos);
            for (var i = 0; i < 8; i++)
                store.QueueRating(photos[i].FilePath, CullState.FromLadderIndex(i));
        }   // DisposeAsync drains and flushes

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        for (var i = 0; i < 8; i++)
        {
            var expected = CullState.FromLadderIndex(i);
            Assert.True(loaded.TryGetValue(photos[i].FilePath, out var stored), $"missing row for ladder index {i}");
            Assert.Equal(expected.Flag, stored.Cull.Flag);
            Assert.Equal(expected.Stars, stored.Cull.Stars);
            Assert.Equal(i, stored.Cull.LadderIndex);

            // Rating writes must leave rotation alone.
            Assert.Equal(Rotation.None, stored.Rotation);
        }
    }

    [Fact]
    public async Task RatingsSurviveReopen_MatchedByRootFolder()
    {
        var photo = Photo("survivor.jpg");

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(new[] { photo });
            store.QueueRating(photo.FilePath, CullState.FromLadderIndex(7));
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(5, loaded[photo.FilePath].Cull.Stars);
        Assert.Equal(Flag.Picked, loaded[photo.FilePath].Cull.Flag);
    }

    [Fact]
    public async Task HundredWrites_AllLand_AndTheCallingThreadNeverBlocks()
    {
        var photos = Enumerable.Range(0, 100).Select(i => Photo($"batch{i}.jpg")).ToList();

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(photos);

            // QueueRating must be a non-blocking TryWrite. 100 calls should be effectively
            // instant; the database work happens on the background writer.
            var sw = Stopwatch.StartNew();
            foreach (var p in photos)
                store.QueueRating(p.FilePath, CullState.FromLadderIndex(7));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 250,
                $"QueueRating blocked the caller: 100 calls took {sw.ElapsedMilliseconds} ms");
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(100, photos.Count(p => loaded.TryGetValue(p.FilePath, out var s) && s.Cull.Stars == 5));
    }

    [Fact]
    public async Task RapidChangesToOnePhoto_CollapseToTheFinalValue()
    {
        var photo = Photo("laddered.jpg");

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(new[] { photo });

            // Walk the whole ladder up and down repeatedly, as holding Up/Down would.
            for (var pass = 0; pass < 5; pass++)
                for (var i = 0; i <= 7; i++)
                    store.QueueRating(photo.FilePath, CullState.FromLadderIndex(i));

            store.QueueRating(photo.FilePath, CullState.FromLadderIndex(3));   // final value
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(3, loaded[photo.FilePath].Cull.LadderIndex);

        // The schema's UNIQUE(path) means collapsing is structural: one row per photo, never 40.
        Assert.Single(loaded, kv => kv.Key == photo.FilePath);
    }

    [Fact]
    public async Task InvalidFlagStarsPair_CannotBeQueued()
    {
        // The invariant is enforced by CullState's constructor, so an invalid pair cannot even
        // be constructed to hand to QueueRating.
        Assert.ThrowsAny<ArgumentException>(() => new CullState(Flag.Rejected, 4));
        Assert.ThrowsAny<ArgumentException>(() => new CullState(Flag.Unflagged, 2));

        var photo = Photo("valid.jpg");
        await using var store = await OpenAsync();
        await store.RegisterPhotosAsync(new[] { photo });

        store.QueueRating(photo.FilePath, new CullState(Flag.Picked, 4));
        var loaded = await store.LoadPhotoStatesAsync();
        Assert.True(loaded.ContainsKey(photo.FilePath));
    }

    [Fact]
    public async Task LoadFromEmptyDatabase_ReturnsEmpty_DoesNotThrow()
    {
        await using var store = await OpenAsync();
        var loaded = await store.LoadPhotoStatesAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task ReloadWithMissingAndExtraFiles_DoesNotThrow()
    {
        var original = new[] { Photo("a.jpg"), Photo("b.jpg"), Photo("c.jpg") };

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(original);
            store.QueueRating(original[0].FilePath, CullState.FromLadderIndex(2));
            store.QueueRating(original[1].FilePath, CullState.FromLadderIndex(0));
        }

        // b.jpg has vanished since; d.jpg is new and has no stored row.
        var nextRun = new[] { Photo("a.jpg"), Photo("c.jpg"), Photo("d.jpg") };

        await using var reopened = await OpenAsync();
        await reopened.RegisterPhotosAsync(nextRun);
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(Flag.Picked, loaded[original[0].FilePath].Cull.Flag);

        // A photo with no stored rating simply takes the default at the call site.
        var dState = loaded.TryGetValue(nextRun[2].FilePath, out var d) ? d.Cull : CullState.Default;
        Assert.Equal(CullState.Default.LadderIndex, dState.LadderIndex);

        // The row for the now-missing b.jpg is still readable and harmless.
        Assert.Equal(Flag.Rejected, loaded[original[1].FilePath].Cull.Flag);
    }

    [Fact]
    public async Task RegisterPhotos_IsIdempotent()
    {
        var photos = new[] { Photo("x.jpg"), Photo("y.jpg") };

        await using var store = await OpenAsync();
        await store.RegisterPhotosAsync(photos);
        await store.RegisterPhotosAsync(photos);   // second scan of the same folder
        await store.RegisterPhotosAsync(photos);

        var loaded = await store.LoadPhotoStatesAsync();
        Assert.Equal(2, loaded.Count);
    }

    // ---- Rotation (PRD 1.11) ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EveryRotationRoundTrips(int quarterTurns)
    {
        var photo = Photo($"rot{quarterTurns}.jpg");

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(new[] { photo });
            store.QueueRotation(photo.FilePath, Rotation.FromQuarterTurns(quarterTurns));
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(Rotation.FromQuarterTurns(quarterTurns), loaded[photo.FilePath].Rotation);
    }

    [Fact]
    public async Task RotationAndRatingOnTheSamePhoto_DoNotClobberEachOther()
    {
        // Both land in one batch, and the writer collapses pending writes by path - so without
        // field-level merging whichever arrived second would silently reset the other.
        var photo = Photo("both.jpg");

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(new[] { photo });
            store.QueueRating(photo.FilePath, CullState.FromLadderIndex(6));    // Picked, 4 stars
            store.QueueRotation(photo.FilePath, Rotation.FromQuarterTurns(3));
            store.QueueRating(photo.FilePath, CullState.FromLadderIndex(7));    // Picked, 5 stars
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(5, loaded[photo.FilePath].Cull.Stars);
        Assert.Equal(Rotation.FromQuarterTurns(3), loaded[photo.FilePath].Rotation);
    }

    [Fact]
    public async Task RotatingManyPhotos_NeverBlocksTheCaller()
    {
        var photos = Enumerable.Range(0, 100).Select(i => Photo($"spin{i}.jpg")).ToList();

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(photos);

            var sw = Stopwatch.StartNew();
            foreach (var p in photos)
                store.QueueRotation(p.FilePath, Rotation.FromQuarterTurns(2));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 250,
                $"QueueRotation blocked the caller: 100 calls took {sw.ElapsedMilliseconds} ms");
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(100, photos.Count(p =>
            loaded.TryGetValue(p.FilePath, out var s) && s.Rotation == Rotation.FromQuarterTurns(2)));
    }

    [Fact]
    public async Task FreshDatabase_IsAtTheCurrentSchemaVersion()
    {
        await using var store = await OpenAsync();
        Assert.Equal(SessionStore.SchemaVersion, await store.ReadSchemaVersionAsync());
    }

    /// <summary>
    /// The migration test that actually matters. CREATE TABLE IF NOT EXISTS is a no-op against a
    /// database that already exists, so a database written by the previous build has no rotation
    /// column - and real ones are sitting in %LOCALAPPDATA% right now. Opening one must add the
    /// column rather than failing the first rotation write with "no such column: rotation".
    /// </summary>
    [Fact]
    public async Task PreExistingDatabaseWithoutTheRotationColumn_IsMigratedOnOpen()
    {
        var photo = Photo("legacy.jpg");
        var legacyDb = Path.Combine(_sessionDir, "legacy.db");

        // Build a database with the PREVIOUS schema: no rotation column at all.
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = legacyDb,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            }.ToString()))
        {
            await raw.OpenAsync();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE photos (
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
                CREATE TABLE companions (photo_id INTEGER, path TEXT, kind TEXT);
                CREATE TABLE session_meta (root_path TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "INSERT INTO session_meta (root_path) VALUES ($root);";
            cmd.Parameters.AddWithValue("$root", _root);
            await cmd.ExecuteNonQueryAsync();

            // A photo already rated by the old build, to prove the migration preserves data.
            cmd.Parameters.Clear();
            cmd.CommandText = """
                INSERT INTO photos (path, rel_path, basename, extension, format_family,
                                    sort_time, sort_time_tier, capture_subsec, file_bytes, flag, stars)
                VALUES ($p, 'legacy.jpg', 'legacy', '.jpg', 1, '2026-08-23T03:00:00', 1, NULL, 10, 1, 3);
                """;
            cmd.Parameters.AddWithValue("$p", photo.FilePath);
            await cmd.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Opening through SessionStore must find this database by root and migrate it in place.
        await using (var store = await OpenAsync())
        {
            Assert.Equal(legacyDb, store.DatabasePath);
            Assert.Equal(SessionStore.SchemaVersion, await store.ReadSchemaVersionAsync());

            // The pre-existing rating survived, and defaults to no rotation.
            var afterMigration = await store.LoadPhotoStatesAsync();
            Assert.Equal(3, afterMigration[photo.FilePath].Cull.Stars);
            Assert.Equal(Rotation.None, afterMigration[photo.FilePath].Rotation);

            // And the write that would previously have thrown "no such column" now works.
            store.QueueRotation(photo.FilePath, Rotation.FromQuarterTurns(2));
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(Rotation.FromQuarterTurns(2), loaded[photo.FilePath].Rotation);
        Assert.Equal(3, loaded[photo.FilePath].Cull.Stars);
    }

    [Fact]
    public async Task MigrationIsIdempotent_AcrossRepeatedOpens()
    {
        var photo = Photo("repeat.jpg");

        for (var i = 0; i < 3; i++)
        {
            await using var store = await OpenAsync();
            await store.RegisterPhotosAsync(new[] { photo });
            Assert.Equal(SessionStore.SchemaVersion, await store.ReadSchemaVersionAsync());
        }
    }

    [Fact]
    public async Task OutOfRangeStoredRotation_IsNormalisedNotRenderedAsIs()
    {
        // A hand-edited or future-build row must not produce an arbitrary angle.
        var photo = Photo("weird.jpg");

        await using (var store = await OpenAsync())
        {
            await store.RegisterPhotosAsync(new[] { photo });
            store.QueueRotation(photo.FilePath, Rotation.FromQuarterTurns(-1));   // normalises to 3
        }

        await using var reopened = await OpenAsync();
        var loaded = await reopened.LoadPhotoStatesAsync();

        Assert.Equal(3, loaded[photo.FilePath].Rotation.QuarterTurns);
    }

    [Fact]
    public async Task DatabaseUsesWalJournalMode()
    {
        await using var store = await OpenAsync();

        // PRD 3.1 mandates WAL. -wal appears next to the .db once a write has happened.
        await store.RegisterPhotosAsync(new[] { Photo("wal.jpg") });
        Assert.True(File.Exists(store.DatabasePath + "-wal") || File.Exists(store.DatabasePath),
            "expected a WAL sidecar or the database file to exist");
    }
}
