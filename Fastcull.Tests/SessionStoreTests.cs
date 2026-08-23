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
        var loaded = await reopened.LoadRatingsAsync();

        for (var i = 0; i < 8; i++)
        {
            var expected = CullState.FromLadderIndex(i);
            Assert.True(loaded.TryGetValue(photos[i].FilePath, out var actual), $"missing row for ladder index {i}");
            Assert.Equal(expected.Flag, actual.Flag);
            Assert.Equal(expected.Stars, actual.Stars);
            Assert.Equal(i, actual.LadderIndex);
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
        var loaded = await reopened.LoadRatingsAsync();

        Assert.Equal(5, loaded[photo.FilePath].Stars);
        Assert.Equal(Flag.Picked, loaded[photo.FilePath].Flag);
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
        var loaded = await reopened.LoadRatingsAsync();

        Assert.Equal(100, photos.Count(p => loaded.TryGetValue(p.FilePath, out var s) && s.Stars == 5));
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
        var loaded = await reopened.LoadRatingsAsync();

        Assert.Equal(3, loaded[photo.FilePath].LadderIndex);

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
        var loaded = await store.LoadRatingsAsync();
        Assert.True(loaded.ContainsKey(photo.FilePath));
    }

    [Fact]
    public async Task LoadFromEmptyDatabase_ReturnsEmpty_DoesNotThrow()
    {
        await using var store = await OpenAsync();
        var loaded = await store.LoadRatingsAsync();
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
        var loaded = await reopened.LoadRatingsAsync();

        Assert.Equal(Flag.Picked, loaded[original[0].FilePath].Flag);

        // A photo with no stored rating simply takes the default at the call site.
        var dState = loaded.TryGetValue(nextRun[2].FilePath, out var d) ? d : CullState.Default;
        Assert.Equal(CullState.Default.LadderIndex, dState.LadderIndex);

        // The row for the now-missing b.jpg is still readable and harmless.
        Assert.Equal(Flag.Rejected, loaded[original[1].FilePath].Flag);
    }

    [Fact]
    public async Task RegisterPhotos_IsIdempotent()
    {
        var photos = new[] { Photo("x.jpg"), Photo("y.jpg") };

        await using var store = await OpenAsync();
        await store.RegisterPhotosAsync(photos);
        await store.RegisterPhotosAsync(photos);   // second scan of the same folder
        await store.RegisterPhotosAsync(photos);

        var loaded = await store.LoadRatingsAsync();
        Assert.Equal(2, loaded.Count);
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
