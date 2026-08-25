using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fastcull.Models;
using Fastcull.Services;
using Xunit;

namespace Fastcull.Tests;

/// <summary>
/// The finish engine, tested against a real temporary filesystem.
///
/// **Weighted deliberately towards the failure paths.** The happy path of a file copier is the
/// part least likely to be wrong and least costly if it is; what matters here is what happens when
/// a verify fails, a delete is refused, the disk fills, or the user hits Cancel - because those are
/// the paths that decide whether somebody's photographs survive. Each of those is provoked
/// deterministically through <see cref="FaultingFileSystem"/> rather than hoped for.
/// </summary>
public sealed class FinishExecutorTests : IDisposable
{
    private readonly string _root;

    public FinishExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fastcull-finish-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    // ------------------------------------------------------------------
    // Corpus helpers
    // ------------------------------------------------------------------

    private string WriteFile(string relative, int sizeBytes = 4096, int seed = 1)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var bytes = new byte[sizeBytes];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);

        return full;
    }

    private FinishPlan PlanOf(FinishOperation operation, params (string Relative, CullState State)[] files)
        => FinishPlanner.Plan(_root, operation,
            files.Select(f => (Path.Combine(_root, f.Relative), f.Relative, f.State)));

    private static readonly CullState Approved = new(Flag.Picked, 0);
    private static readonly CullState Rejected = CullState.Default.AsRejected();
    private static readonly CullState ThreeStar = new(Flag.Picked, 3);
    private static readonly CullState Unrated = CullState.Default;

    private static bool SameBytes(string a, string b)
        => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    // ------------------------------------------------------------------
    // Copy
    // ------------------------------------------------------------------

    [Fact]
    public async Task CopyLeavesEveryOriginalAndProducesIdenticalBytes()
    {
        var a = WriteFile("a.jpg", 100_000, 1);
        var b = WriteFile(Path.Combine("CardA", "b.jpg"), 60_000, 2);
        var original = File.ReadAllBytes(a);

        var plan = PlanOf(FinishOperation.Copy, ("a.jpg", Approved), (Path.Combine("CardA", "b.jpg"), ThreeStar));
        var report = await FinishExecutor.ExecuteAsync(plan, new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(2, report.DoneCount);

        Assert.True(File.Exists(a), "copy must never remove an original");
        Assert.True(File.Exists(b));
        Assert.Equal(original, File.ReadAllBytes(a));

        var copiedA = Path.Combine(_root, "Approved", "a.jpg");
        var copiedB = Path.Combine(_root, "Rated", "3", "CardA", "b.jpg");

        Assert.True(File.Exists(copiedA));
        Assert.True(File.Exists(copiedB), "the subfolder path must be preserved inside the bucket");
        Assert.True(SameBytes(a, copiedA));
        Assert.True(SameBytes(b, copiedB));
    }

    [Fact]
    public async Task CopyPreservesTheLastWriteTime()
    {
        // The sort order in section 1.3 keys on this; a copy that resets it silently reorders a shoot.
        var a = WriteFile("a.jpg");
        var stamp = new DateTime(2019, 4, 3, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(a, stamp);

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Copy, ("a.jpg", Approved)), new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "Approved", "a.jpg")));
    }

    [Fact]
    public async Task UnratedFilesAreLeftExactlyWhereTheyAre()
    {
        var keep = WriteFile("undecided.jpg");
        var before = File.ReadAllBytes(keep);

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Move, ("undecided.jpg", Unrated)), new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.True(File.Exists(keep));
        Assert.Equal(before, File.ReadAllBytes(keep));
        Assert.Contains(report.Results, r => r.Status == FinishFileStatus.LeftInPlace);
    }

    // ------------------------------------------------------------------
    // Move
    // ------------------------------------------------------------------

    [Fact]
    public async Task MoveRemovesTheOriginalOnlyAfterAVerifiedCopyExists()
    {
        var a = WriteFile("a.jpg", 250_000, 7);
        var expected = File.ReadAllBytes(a);

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Move, ("a.jpg", Rejected)), new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.False(File.Exists(a), "the original should be gone after a successful move");

        var moved = Path.Combine(_root, "Rejected", "a.jpg");
        Assert.True(File.Exists(moved));
        Assert.Equal(expected, File.ReadAllBytes(moved));
    }

    [Fact]
    public async Task AFailedVerifyLeavesTheOriginalAndRemovesThePartialCopy()
    {
        // The single most important test in this file: if the copy cannot be trusted, the original
        // must survive and the bad copy must not be left lying around looking like a result.
        var a = WriteFile("a.jpg", 50_000, 3);
        var expected = File.ReadAllBytes(a);

        var fs = new FaultingFileSystem { CorruptHashFor = "a.jpg" };
        var report = await FinishExecutor.ExecuteAsync(PlanOf(FinishOperation.Move, ("a.jpg", Approved)), fs);

        Assert.Equal(FinishOutcome.Failed, report.Outcome);
        Assert.Equal(1, report.FailedCount);
        Assert.Contains("hash mismatch", report.Results.Single(r => r.Status == FinishFileStatus.Failed).Error);

        Assert.True(File.Exists(a), "ORIGINAL MUST SURVIVE a failed verify");
        Assert.Equal(expected, File.ReadAllBytes(a));
        Assert.False(File.Exists(Path.Combine(_root, "Approved", "a.jpg")), "the unverified copy must be removed");
    }

    [Fact]
    public async Task ALengthMismatchIsCaughtEvenIfTheHashWouldNotBe()
    {
        var a = WriteFile("a.jpg", 40_000, 5);

        var fs = new FaultingFileSystem { TruncateLengthFor = "a.jpg" };
        var report = await FinishExecutor.ExecuteAsync(PlanOf(FinishOperation.Move, ("a.jpg", Approved)), fs);

        Assert.Equal(FinishOutcome.Failed, report.Outcome);
        Assert.Contains("verify failed", report.Results.Single(r => r.Status == FinishFileStatus.Failed).Error);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task AnOriginalThatCannotBeDeletedKeepsBothCopiesAndSaysSo()
    {
        // The copy is verified, so the photo is safe in two places. Deleting the good copy to
        // "tidy up" would throw away the only proof the copy worked.
        var a = WriteFile("a.jpg", 20_000, 9);

        var fs = new FaultingFileSystem { FailDeleteOfSource = "a.jpg" };
        var report = await FinishExecutor.ExecuteAsync(PlanOf(FinishOperation.Move, ("a.jpg", Approved)), fs);

        Assert.Equal(FinishOutcome.Failed, report.Outcome);
        Assert.True(File.Exists(a), "the original is still there because the delete failed");
        Assert.True(File.Exists(Path.Combine(_root, "Approved", "a.jpg")), "the verified copy is kept");

        var failure = report.Results.Single(r => r.Status == FinishFileStatus.Failed);
        Assert.Contains("could not be deleted", failure.Error);
    }

    // ------------------------------------------------------------------
    // Partial failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task AFailureStopsTheRunAndLeavesEveryUnreachedOriginalUntouched()
    {
        var files = Enumerable.Range(1, 6).Select(i => $"p{i}.jpg").ToArray();
        foreach (var (f, i) in files.Select((f, i) => (f, i))) WriteFile(f, 10_000, i + 1);

        var plan = PlanOf(FinishOperation.Move, files.Select(f => (f, Approved)).ToArray());
        var target = plan.Moves.ElementAt(3).RelativePath;

        var fs = new FaultingFileSystem { ThrowOnCopyOf = target };
        var report = await FinishExecutor.ExecuteAsync(plan, fs);

        Assert.Equal(FinishOutcome.Failed, report.Outcome);
        Assert.Equal(3, report.DoneCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(2, report.NotAttemptedCount);

        // The failed file and everything after it still have their originals.
        foreach (var r in report.Results.Where(r => r.Status is FinishFileStatus.Failed or FinishFileStatus.NotAttempted))
            Assert.True(File.Exists(r.Entry.SourcePath), $"{r.Entry.RelativePath} must still exist");

        // Work already completed is not rolled back.
        foreach (var r in report.Results.Where(r => r.Status == FinishFileStatus.Done))
        {
            Assert.True(File.Exists(r.FinalDestination!));
            Assert.False(File.Exists(r.Entry.SourcePath));
        }
    }

    [Fact]
    public async Task NoFileEverEndsUpInNeitherPlace()
    {
        // Stated as the property that actually matters, over every outcome the engine can reach.
        var files = Enumerable.Range(1, 5).Select(i => $"q{i}.jpg").ToArray();
        foreach (var (f, i) in files.Select((f, i) => (f, i))) WriteFile(f, 8_000, i + 20);

        var plan = PlanOf(FinishOperation.Move, files.Select(f => (f, Rejected)).ToArray());
        var fs = new FaultingFileSystem { ThrowOnCopyOf = plan.Moves.ElementAt(2).RelativePath };
        var report = await FinishExecutor.ExecuteAsync(plan, fs);

        foreach (var r in report.Results.Where(r => r.Entry.Bucket != FinishBucket.Untouched))
        {
            var atSource = File.Exists(r.Entry.SourcePath);
            var atDestination = r.FinalDestination is not null && File.Exists(r.FinalDestination);

            Assert.True(atSource || atDestination,
                $"{r.Entry.RelativePath} exists in NEITHER place - status {r.Status}");
        }
    }

    [Fact]
    public async Task AFailureWritesAFailureReportAtTheSourceRoot()
    {
        WriteFile("a.jpg");
        WriteFile("b.jpg");

        var plan = PlanOf(FinishOperation.Move, ("a.jpg", Approved), ("b.jpg", Approved));
        var fs = new FaultingFileSystem { ThrowOnCopyOf = plan.Moves.First().RelativePath };
        var report = await FinishExecutor.ExecuteAsync(plan, fs);

        var reportPath = Path.Combine(_root, "failure report.txt");
        Assert.Equal(reportPath, report.FailureReportPath);
        Assert.True(File.Exists(reportPath));

        var text = File.ReadAllText(reportPath);
        Assert.Contains("DID NOT COMPLETE", text);
        Assert.Contains("No photo has been lost", text);
        Assert.Contains("COULD NOT BE PROCESSED", text);
        Assert.Contains("NOT REACHED", text);
    }

    [Fact]
    public async Task ASuccessfulRunWritesNoFailureReport()
    {
        WriteFile("a.jpg");
        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Copy, ("a.jpg", Approved)), new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Null(report.FailureReportPath);
        Assert.False(File.Exists(Path.Combine(_root, "failure report.txt")));
    }

    // ------------------------------------------------------------------
    // Collisions
    // ------------------------------------------------------------------

    [Fact]
    public async Task AnExistingDestinationIsNeverOverwritten()
    {
        // A second Finish Session meets the first one's output, and that output is real photographs.
        var a = WriteFile("a.jpg", 30_000, 11);

        var existingDirectory = Path.Combine(_root, "Approved");
        Directory.CreateDirectory(existingDirectory);
        var existing = Path.Combine(existingDirectory, "a.jpg");
        var existingBytes = new byte[9_000];
        new Random(999).NextBytes(existingBytes);
        File.WriteAllBytes(existing, existingBytes);

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Move, ("a.jpg", Approved)), new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(existingBytes, File.ReadAllBytes(existing));   // untouched

        var renamed = Path.Combine(existingDirectory, "a_1.jpg");
        Assert.True(File.Exists(renamed));
        Assert.Equal(1, report.RenamedCount);
        Assert.Equal(renamed, report.Results.Single(r => r.Status == FinishFileStatus.Done).FinalDestination);
    }

    [Fact]
    public async Task TwoFilesLandingOnOneNameBothSurvive()
    {
        // Distinct sources whose planned destinations coincide - each must get its own name.
        var one = WriteFile(Path.Combine("CardA", "DSC_0001.ARW"), 12_000, 31);
        var two = WriteFile(Path.Combine("CardB", "DSC_0001.ARW"), 15_000, 32);

        // A plan that deliberately collapses both to the same relative path, which is the shape a
        // genuine collision takes.
        var plan = FinishPlanner.Plan(_root, FinishOperation.Copy, new[]
        {
            (one, "DSC_0001.ARW", Approved),
            (two, "DSC_0001.ARW", Approved),
        });

        var report = await FinishExecutor.ExecuteAsync(plan, new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(2, report.DoneCount);
        Assert.Equal(1, report.RenamedCount);

        var first = Path.Combine(_root, "Approved", "DSC_0001.ARW");
        var second = Path.Combine(_root, "Approved", "DSC_0001_1.ARW");

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(SameBytes(one, first));
        Assert.True(SameBytes(two, second), "the second file's own bytes must be what landed, not a duplicate of the first");
    }

    [Fact]
    public async Task FlatStructureCollidesOnEveryCardAndLosesNothing()
    {
        // PRD 4.2.2's hazard, end to end through the real planner rather than a hand-built plan:
        // three cards each holding DSC_0001.ARW, flattened into one folder. All three must survive
        // with their own bytes. This is the case that makes Flat worth testing separately - under
        // Preserve these three never meet at all.
        var a = WriteFile(Path.Combine("Card1", "DSC_0001.ARW"), 11_000, 41);
        var b = WriteFile(Path.Combine("Card2", "DSC_0001.ARW"), 12_000, 42);
        var c = WriteFile(Path.Combine("Card3", "DSC_0001.ARW"), 13_000, 43);

        var plan = FinishPlanner.Plan(_root, FinishOperation.Copy, new[]
        {
            (a, Path.Combine("Card1", "DSC_0001.ARW"), Approved),
            (b, Path.Combine("Card2", "DSC_0001.ARW"), Approved),
            (c, Path.Combine("Card3", "DSC_0001.ARW"), Approved),
        }, FinishStructure.Flat);

        var report = await FinishExecutor.ExecuteAsync(plan, new SystemFinishFileSystem());

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(3, report.DoneCount);
        Assert.Equal(2, report.RenamedCount);

        var landed = Directory.GetFiles(Path.Combine(_root, "Approved"));
        Assert.Equal(3, landed.Length);

        // Every original is still readable somewhere in the output, byte for byte, and no two
        // destinations hold the same content.
        foreach (var source in new[] { a, b, c })
            Assert.True(landed.Any(d => SameBytes(source, d)),
                $"{Path.GetFileName(Path.GetDirectoryName(source))}'s bytes did not survive the flatten");

        Assert.Equal(3, landed.Select(f => new FileInfo(f).Length).Distinct().Count());
    }

    [Fact]
    public async Task FlatStructureIsNamedInTheRunLog()
    {
        WriteFile(Path.Combine("Card1", "a.jpg"));

        var plan = FinishPlanner.Plan(_root, FinishOperation.Copy,
            new[] { (Path.Combine(_root, "Card1", "a.jpg"), Path.Combine("Card1", "a.jpg"), Approved) },
            FinishStructure.Flat);

        var report = await FinishExecutor.ExecuteAsync(plan, new SystemFinishFileSystem());

        // A Flat run can rename dozens of files legitimately; the log has to say so, or the reader
        // will take a wall of renames for a fault.
        Assert.Contains("FLAT", FinishExecutor.RenderLog(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenamesAreRecordedInTheLog()
    {
        WriteFile("a.jpg");
        Directory.CreateDirectory(Path.Combine(_root, "Approved"));
        File.WriteAllBytes(Path.Combine(_root, "Approved", "a.jpg"), new byte[10]);

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Copy, ("a.jpg", Approved)), new SystemFinishFileSystem());

        Assert.Contains("RENAMED to avoid overwriting", FinishExecutor.RenderLog(report));
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task CancelStopsAtAFileBoundaryAndLeavesTheRestUntouched()
    {
        var files = Enumerable.Range(1, 6).Select(i => $"c{i}.jpg").ToArray();
        foreach (var (f, i) in files.Select((f, i) => (f, i))) WriteFile(f, 10_000, i + 40);

        var plan = PlanOf(FinishOperation.Move, files.Select(f => (f, Approved)).ToArray());

        using var cts = new CancellationTokenSource();
        var fs = new FaultingFileSystem { CancelAfterCopies = (2, cts) };

        var report = await FinishExecutor.ExecuteAsync(plan, fs, null, cts.Token);

        Assert.Equal(FinishOutcome.Cancelled, report.Outcome);
        Assert.Equal(2, report.DoneCount);

        // Every file not completed still has its original, and no destination was left behind.
        foreach (var r in report.Results.Where(r => r.Status != FinishFileStatus.Done))
        {
            Assert.True(File.Exists(r.Entry.SourcePath), $"{r.Entry.RelativePath} original must survive a cancel");
            if (r.Entry.DestinationPath is not null)
                Assert.False(File.Exists(r.Entry.DestinationPath), "no partial destination may be left behind");
        }
    }

    [Fact]
    public async Task CancelMidCopyLeavesNoHalfWrittenFile()
    {
        var a = WriteFile("big.jpg", 400_000, 77);
        var expected = File.ReadAllBytes(a);

        using var cts = new CancellationTokenSource();
        var fs = new FaultingFileSystem { CancelDuringCopyOf = "big.jpg", Cancellation = cts };

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Move, ("big.jpg", Approved)), fs, null, cts.Token);

        Assert.Equal(FinishOutcome.Cancelled, report.Outcome);
        Assert.True(File.Exists(a), "the original must survive a cancel mid-copy");
        Assert.Equal(expected, File.ReadAllBytes(a));
        Assert.False(File.Exists(Path.Combine(_root, "Approved", "big.jpg")), "the half-written copy must be removed");
        Assert.Equal(0, report.DoneCount);
    }

    [Fact]
    public async Task ACancelledRunStillWritesAReport()
    {
        WriteFile("a.jpg");
        WriteFile("b.jpg");

        using var cts = new CancellationTokenSource();
        var fs = new FaultingFileSystem { CancelAfterCopies = (1, cts) };

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Move, ("a.jpg", Approved), ("b.jpg", Approved)), fs, null, cts.Token);

        Assert.Equal(FinishOutcome.Cancelled, report.Outcome);
        Assert.True(File.Exists(Path.Combine(_root, "failure report.txt")));
        Assert.Contains("CANCELLED", File.ReadAllText(Path.Combine(_root, "failure report.txt")));
    }

    // ------------------------------------------------------------------
    // Free space
    // ------------------------------------------------------------------

    [Fact]
    public async Task NotEnoughSpaceRefusesToStartAndWritesNothing()
    {
        var a = WriteFile("a.jpg", 100_000, 4);
        var before = File.ReadAllBytes(a);

        var fs = new FaultingFileSystem { FreeSpaceOverride = 1024 };
        var report = await FinishExecutor.ExecuteAsync(PlanOf(FinishOperation.Move, ("a.jpg", Approved)), fs);

        Assert.Equal(FinishOutcome.RefusedNotEnoughSpace, report.Outcome);
        Assert.Contains("Not enough space", report.Message);
        Assert.Equal(0, report.DoneCount);

        Assert.True(File.Exists(a));
        Assert.Equal(before, File.ReadAllBytes(a));
        Assert.False(Directory.Exists(Path.Combine(_root, "Approved")), "nothing should have been created");
    }

    [Fact]
    public async Task UnknownFreeSpaceDoesNotBlockTheRun()
    {
        // A network destination that cannot be queried must not be treated as full.
        WriteFile("a.jpg");
        var fs = new FaultingFileSystem { FreeSpaceIsUnknown = true };

        var report = await FinishExecutor.ExecuteAsync(PlanOf(FinishOperation.Copy, ("a.jpg", Approved)), fs);

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
    }

    // ------------------------------------------------------------------
    // Progress
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProgressIsReportedForEveryFileAndEndsAtTheTotal()
    {
        foreach (var i in Enumerable.Range(1, 4)) WriteFile($"r{i}.jpg", 5_000, i);

        var seen = new List<FinishProgress>();
        var progress = new Progress<FinishProgress>(p => { lock (seen) seen.Add(p); });

        // Progress<T> posts to the captured context; collect synchronously instead so the
        // assertions are not racing the callbacks.
        var sink = new ImmediateProgress(p => seen.Add(p));

        var report = await FinishExecutor.ExecuteAsync(
            PlanOf(FinishOperation.Copy, Enumerable.Range(1, 4).Select(i => ($"r{i}.jpg", Approved)).ToArray()),
            new SystemFinishFileSystem(), sink);

        Assert.Equal(FinishOutcome.Completed, report.Outcome);
        Assert.Equal(4, seen[^1].Total);
        Assert.Equal(4, seen[^1].Done);
        Assert.True(seen.Count >= 5, "one report per file plus a final one");
        _ = progress;
    }

    private sealed class ImmediateProgress(Action<FinishProgress> onReport) : IProgress<FinishProgress>
    {
        public void Report(FinishProgress value) => onReport(value);
    }

    // ------------------------------------------------------------------
    // Fault injection
    // ------------------------------------------------------------------

    /// <summary>
    /// Wraps the real filesystem and misbehaves on demand.
    ///
    /// Everything not being faulted goes through to real IO, so these tests still exercise real
    /// copies of real bytes - only the specific failure under test is synthetic.
    /// </summary>
    private sealed class FaultingFileSystem : IFinishFileSystem
    {
        private readonly SystemFinishFileSystem _real = new();
        private int _copies;

        public string? ThrowOnCopyOf { get; init; }
        public string? CorruptHashFor { get; init; }
        public string? TruncateLengthFor { get; init; }
        public string? FailDeleteOfSource { get; init; }
        public string? CancelDuringCopyOf { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public (int After, CancellationTokenSource Cts)? CancelAfterCopies { get; init; }
        public long? FreeSpaceOverride { get; init; }
        public bool FreeSpaceIsUnknown { get; init; }

        private static bool Matches(string? needle, string path)
            => needle is not null && path.Replace('/', '\\').EndsWith(needle.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

        public bool FileExists(string path) => _real.FileExists(path);
        public void CreateDirectory(string path) => _real.CreateDirectory(path);
        public DateTime GetLastWriteTimeUtc(string path) => _real.GetLastWriteTimeUtc(path);
        public void SetLastWriteTimeUtc(string path, DateTime utc) => _real.SetLastWriteTimeUtc(path, utc);
        public void WriteAllText(string path, string contents) => _real.WriteAllText(path, contents);

        public long GetFileLength(string path)
        {
            var length = _real.GetFileLength(path);

            // Report the DESTINATION as short, which is what a truncated write looks like.
            if (Matches(TruncateLengthFor, path) && path.Contains("Approved", StringComparison.OrdinalIgnoreCase))
                return length - 1;

            return length;
        }

        public async Task<byte[]> CopyAsync(string source, string destination, CancellationToken cancellationToken)
        {
            if (Matches(ThrowOnCopyOf, source)) throw new IOException("injected copy failure");

            if (Matches(CancelDuringCopyOf, source))
            {
                // Create the partial destination first, so the test proves the engine cleans it up
                // rather than merely never having made it.
                await using (var partial = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    await partial.WriteAsync(new byte[1024], cancellationToken);

                Cancellation!.Cancel();
                throw new OperationCanceledException(Cancellation.Token);
            }

            var hash = await _real.CopyAsync(source, destination, cancellationToken).ConfigureAwait(false);

            _copies++;
            if (CancelAfterCopies is { } c && _copies >= c.After) c.Cts.Cancel();

            return hash;
        }

        public async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
        {
            var hash = await _real.HashAsync(path, cancellationToken).ConfigureAwait(false);

            if (Matches(CorruptHashFor, path) || (CorruptHashFor is not null && path.Contains("Approved", StringComparison.OrdinalIgnoreCase)))
                hash[0] ^= 0xFF;

            return hash;
        }

        public void DeleteFile(string path)
        {
            if (Matches(FailDeleteOfSource, path) && !path.Contains("Approved", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("injected delete failure");

            _real.DeleteFile(path);
        }

        public long? GetAvailableFreeSpace(string path)
            => FreeSpaceIsUnknown ? null : FreeSpaceOverride ?? _real.GetAvailableFreeSpace(path);
    }
}
