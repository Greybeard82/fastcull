using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fastcull.Services
{
    /// <summary>What happened to one file.</summary>
    public enum FinishFileStatus
    {
        /// <summary>Not reached - the run stopped or was cancelled first. The original is untouched.</summary>
        NotAttempted,

        /// <summary>Copied and verified. On a Move, the original is also gone.</summary>
        Done,

        /// <summary>Unrated: deliberately left where it is.</summary>
        LeftInPlace,

        /// <summary>Attempted and failed. The original is untouched; any partial copy was removed.</summary>
        Failed,
    }

    /// <param name="FinalDestination">Where it actually went, which differs from the plan when a collision forced a rename.</param>
    /// <param name="RenamedFrom">The colliding name that was avoided, or null.</param>
    public sealed record FinishFileResult(
        FinishPlanEntry Entry,
        FinishFileStatus Status,
        string? FinalDestination,
        string? RenamedFrom,
        string? Error);

    public enum FinishOutcome
    {
        Completed,
        Cancelled,
        Failed,
        RefusedNotEnoughSpace,
    }

    public sealed record FinishProgress(int Done, int Total, string CurrentFile);

    public sealed class FinishRunReport
    {
        public required FinishOutcome Outcome { get; init; }
        public required FinishOperation Operation { get; init; }
        public required string SourceRoot { get; init; }

        /// <summary>
        /// Carried from the plan so the log says which layout produced these destinations. Under
        /// Flat a run can legitimately rename dozens of files, and a reader who does not know the
        /// mode would read that as something having gone wrong.
        /// </summary>
        public FinishStructure Structure { get; init; } = FinishStructure.Preserve;
        public required IReadOnlyList<FinishFileResult> Results { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public required long BytesWritten { get; init; }

        /// <summary>Set when the run refused to start or stopped early.</summary>
        public string? Message { get; init; }

        /// <summary>Where the completion log was written, if it could be.</summary>
        public string? LogPath { get; set; }

        /// <summary>Where the failure report was written, if one was needed and could be.</summary>
        public string? FailureReportPath { get; set; }

        public int DoneCount => Results.Count(r => r.Status == FinishFileStatus.Done);
        public int FailedCount => Results.Count(r => r.Status == FinishFileStatus.Failed);
        public int NotAttemptedCount => Results.Count(r => r.Status == FinishFileStatus.NotAttempted);
        public int RenamedCount => Results.Count(r => r.RenamedFrom is not null);
    }

    /// <summary>
    /// PRD 4.4. Performs the plan §4.3 computed - the most destructive code in the app.
    ///
    /// **The core invariant: a Move is never a move.** It is copy, then verify, then delete the
    /// original, one file at a time and strictly in that order. An original is deleted only after
    /// its copy has been read back and its bytes confirmed. There is no rename fast path even when
    /// source and destination share a volume: a rename would skip verification entirely, and
    /// having two safety standards depending on which drive the card happened to be in is worth
    /// far less than the seconds it saves.
    ///
    /// **Fail-safe means literally that.** On any error the run stops where it is. Files already
    /// copied-verified-deleted stay done; every file not yet reached keeps its original exactly
    /// where it was. There is no state in which a photo exists in neither place - the only window
    /// where a file is in two places is between the verify and the delete, and a crash there
    /// leaves the ORIGINAL intact, which is the safe direction to fail.
    ///
    /// **Nothing is ever overwritten.** Destinations are opened <c>FileMode.CreateNew</c>, so the
    /// filesystem enforces it even if the collision check were wrong.
    /// </summary>
    public static class FinishExecutor
    {
        /// <summary>Head-room demanded beyond the exact byte count, for filesystem overhead.</summary>
        private const long FreeSpaceMarginBytes = 64L * 1024 * 1024;

        public static async Task<FinishRunReport> ExecuteAsync(
            FinishPlan plan,
            IFinishFileSystem fs,
            IProgress<FinishProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(fs);

            var clock = Stopwatch.StartNew();
            var results = new List<FinishFileResult>();
            var moves = plan.Moves.ToList();

            // Unrated photos are recorded as deliberately untouched rather than omitted, so the
            // log can answer "why was this one not sorted?".
            foreach (var untouched in plan.Entries.Where(e => e.Bucket == FinishBucket.Untouched))
                results.Add(new FinishFileResult(untouched, FinishFileStatus.LeftInPlace, null, null, null));

            if (plan.Operation == FinishOperation.None)
            {
                return Finalise(plan, fs, results, FinishOutcome.Failed, clock.Elapsed, 0,
                    "No operation was chosen. Nothing was done.");
            }

            // ---- Free space, before anything is written ----
            var (spaceOk, required, available, spaceMessage) = CheckFreeSpace(plan, fs, moves);
            if (!spaceOk)
            {
                foreach (var entry in moves)
                    results.Add(new FinishFileResult(entry, FinishFileStatus.NotAttempted, null, null, "not attempted"));

                return Finalise(plan, fs, results, FinishOutcome.RefusedNotEnoughSpace, clock.Elapsed, 0, spaceMessage);
            }

            // ---- The run ----
            //
            // Tracks destinations claimed during THIS run as well as what is already on disk, so
            // two files cannot be handed the same free name.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytesWritten = 0;
            var outcome = FinishOutcome.Completed;
            string? stopMessage = null;
            var stoppedAt = -1;

            for (var i = 0; i < moves.Count; i++)
            {
                // Cancellation is observed BETWEEN files only. Inside a file the copy is either
                // completed or its partial destination is removed - there is no third state.
                if (cancellationToken.IsCancellationRequested)
                {
                    outcome = FinishOutcome.Cancelled;
                    stopMessage = $"Cancelled after {results.Count(r => r.Status == FinishFileStatus.Done)} of {moves.Count} files.";
                    stoppedAt = i;
                    break;
                }

                var entry = moves[i];
                progress?.Report(new FinishProgress(i, moves.Count, entry.RelativePath));

                var result = await ProcessOneAsync(entry, plan.Operation, fs, claimed, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(result);

                if (result.Status == FinishFileStatus.Done)
                {
                    try { bytesWritten += fs.FileExists(result.FinalDestination!) ? fs.GetFileLength(result.FinalDestination!) : 0; }
                    catch (Exception) { /* accounting only */ }
                    continue;
                }

                // A cancel that landed mid-copy surfaces here as a failure; report it as a cancel,
                // because that is what the user did and the state is identical either way.
                if (cancellationToken.IsCancellationRequested)
                {
                    outcome = FinishOutcome.Cancelled;
                    stopMessage = $"Cancelled during {entry.RelativePath}. Its original is untouched.";
                }
                else
                {
                    outcome = FinishOutcome.Failed;
                    stopMessage = $"Stopped at {entry.RelativePath}: {result.Error}";
                }

                stoppedAt = i + 1;
                break;
            }

            // Everything after the stopping point was never touched.
            if (stoppedAt >= 0)
            {
                for (var i = stoppedAt; i < moves.Count; i++)
                    results.Add(new FinishFileResult(moves[i], FinishFileStatus.NotAttempted, null, null, "not attempted"));
            }

            progress?.Report(new FinishProgress(moves.Count, moves.Count, string.Empty));

            return Finalise(plan, fs, results, outcome, clock.Elapsed, bytesWritten, stopMessage);
        }

        /// <summary>
        /// Copy, verify, and - only for a Move, and only after the verify - delete the original.
        ///
        /// Every failure path removes the partial destination before returning, so a failed file
        /// leaves nothing behind at the destination and its original untouched.
        /// </summary>
        private static async Task<FinishFileResult> ProcessOneAsync(
            FinishPlanEntry entry,
            FinishOperation operation,
            IFinishFileSystem fs,
            HashSet<string> claimed,
            CancellationToken cancellationToken)
        {
            var planned = entry.DestinationPath!;
            string? destination = null;

            try
            {
                var directory = Path.GetDirectoryName(planned);
                if (!string.IsNullOrEmpty(directory)) fs.CreateDirectory(directory);

                destination = ResolveCollision(planned, fs, claimed, out var renamedFrom);
                claimed.Add(destination);

                var sourceLength = fs.GetFileLength(entry.SourcePath);
                var sourceStamp = fs.GetLastWriteTimeUtc(entry.SourcePath);

                var sourceHash = await fs.CopyAsync(entry.SourcePath, destination, cancellationToken)
                    .ConfigureAwait(false);

                // ---- Past this point the file is seen through to the end, cancel or not. ----
                //
                // "Stop at a file boundary, never mid-file" taken literally: the copy has already
                // succeeded, so abandoning it here would throw that work away and leave the file
                // untouched for no benefit - the state would be safe either way. Finishing the
                // verify and the delete is what makes the boundary a boundary. The cancel is
                // observed at the top of the next iteration instead.
                //
                // Only the copy is cancellable, and that is where all the time goes; verifying is
                // a straight read of a file that was just written.

                // ---- Verify. Nothing is deleted until this passes. ----
                var destinationLength = fs.GetFileLength(destination);
                if (destinationLength != sourceLength)
                {
                    fs.DeleteFile(destination);
                    return Fail(entry, renamedFrom,
                        $"verify failed: copied {destinationLength} bytes, expected {sourceLength}");
                }

                var destinationHash = await fs.HashAsync(destination, CancellationToken.None).ConfigureAwait(false);
                if (!destinationHash.AsSpan().SequenceEqual(sourceHash))
                {
                    fs.DeleteFile(destination);
                    return Fail(entry, renamedFrom, "verify failed: content hash mismatch");
                }

                // Carry the capture time across. Done after verification so a failure to set it
                // cannot be confused with a failure to copy, and checked because a wrong timestamp
                // silently reorders a shoot (§1.3 sorts on it).
                try
                {
                    fs.SetLastWriteTimeUtc(destination, sourceStamp);
                }
                catch (Exception ex)
                {
                    fs.DeleteFile(destination);
                    return Fail(entry, renamedFrom, $"could not preserve timestamp: {ex.Message}");
                }

                // ---- Only now may the original go ----
                if (operation == FinishOperation.Move)
                {
                    try
                    {
                        fs.DeleteFile(entry.SourcePath);
                    }
                    catch (Exception ex)
                    {
                        // The copy is good, so the photo is safe in two places. Leaving the
                        // verified copy and reporting is right: deleting it to "tidy up" would
                        // throw away the only proof the copy succeeded, and the original is still
                        // there regardless.
                        return new FinishFileResult(entry, FinishFileStatus.Failed, destination, renamedFrom,
                            $"copied and verified, but the original could not be deleted: {ex.Message}");
                    }
                }

                return new FinishFileResult(entry, FinishFileStatus.Done, destination, renamedFrom, null);
            }
            catch (OperationCanceledException)
            {
                RemovePartial(fs, destination);
                return Fail(entry, null, "cancelled");
            }
            catch (Exception ex)
            {
                RemovePartial(fs, destination);
                return Fail(entry, null, ex.Message);
            }
        }

        private static FinishFileResult Fail(FinishPlanEntry entry, string? renamedFrom, string error)
            => new(entry, FinishFileStatus.Failed, null, renamedFrom, error);

        /// <summary>
        /// Removes a destination that was being written when something went wrong.
        ///
        /// Safe by construction: destinations are only ever newly created files (CreateNew), so
        /// this can never delete anything that existed before the run.
        /// </summary>
        private static void RemovePartial(IFinishFileSystem fs, string? destination)
        {
            if (destination is null) return;

            try
            {
                if (fs.FileExists(destination)) fs.DeleteFile(destination);
            }
            catch (Exception)
            {
                // Best effort. A leftover partial at the destination is untidy; failing here and
                // masking the original error would be worse.
            }
        }

        /// <summary>
        /// Finds a free destination name. Never returns a path that exists or that another file in
        /// this run has already claimed.
        ///
        /// Relative-path preservation (§4.3) removes most collisions, but not all: a second Finish
        /// Session over the same folder meets the first one's output, and that output is real
        /// photographs. Suffixing is the only acceptable answer - overwriting one is not.
        /// </summary>
        private static string ResolveCollision(string planned, IFinishFileSystem fs, HashSet<string> claimed,
                                               out string? renamedFrom)
        {
            renamedFrom = null;

            if (!fs.FileExists(planned) && !claimed.Contains(planned)) return planned;

            var directory = Path.GetDirectoryName(planned) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(planned);
            var extension = Path.GetExtension(planned);

            for (var n = 1; n < 10_000; n++)
            {
                var candidate = Path.Combine(directory, $"{stem}_{n}{extension}");
                if (!fs.FileExists(candidate) && !claimed.Contains(candidate))
                {
                    renamedFrom = planned;
                    return candidate;
                }
            }

            throw new IOException($"could not find a free name for {planned} after 10000 attempts");
        }

        private static (bool Ok, long Required, long? Available, string? Message) CheckFreeSpace(
            FinishPlan plan, IFinishFileSystem fs, IReadOnlyList<FinishPlanEntry> moves)
        {
            long required = 0;

            foreach (var entry in moves)
            {
                try { required += fs.GetFileLength(entry.SourcePath); }
                catch (Exception) { /* a missing source fails later, per file, with a real message */ }
            }

            var available = fs.GetAvailableFreeSpace(plan.SourceRoot);

            // Unknown free space is not treated as zero. Refusing to run because the volume could
            // not be queried would block a perfectly good network destination.
            if (available is null) return (true, required, null, null);

            // A Move needs the full amount too: every file is copied before its original is
            // deleted, so the peak requirement is the same as a Copy.
            var needed = required + FreeSpaceMarginBytes;
            if (available >= needed) return (true, required, available, null);

            return (false, required, available,
                $"Not enough space. {Bytes(required)} needed ({Bytes(FreeSpaceMarginBytes)} head-room on top), "
                + $"but only {Bytes(available.Value)} free on the destination. Nothing was moved or copied.");
        }

        private static string Bytes(long value)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double v = value;
            var u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.#} {units[u]}";
        }

        // ------------------------------------------------------------------
        // Reports
        // ------------------------------------------------------------------

        private static FinishRunReport Finalise(
            FinishPlan plan, IFinishFileSystem fs, List<FinishFileResult> results,
            FinishOutcome outcome, TimeSpan elapsed, long bytesWritten, string? message)
        {
            var report = new FinishRunReport
            {
                Outcome = outcome,
                Operation = plan.Operation,
                Structure = plan.Structure,
                SourceRoot = plan.SourceRoot,
                Results = results,
                Elapsed = elapsed,
                BytesWritten = bytesWritten,
                Message = message,
            };

            // A record of what happened is written whatever the outcome - success, failure,
            // cancellation. An operation on somebody's photographs that leaves no trace is not
            // acceptable even when it worked.
            report.LogPath = TryWrite(fs,
                Path.Combine(FinishPlanner.LogDirectory, $"finish-run-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
                RenderLog(report),
                createDirectory: true);

            if (outcome is FinishOutcome.Failed or FinishOutcome.Cancelled or FinishOutcome.RefusedNotEnoughSpace)
            {
                // At the SOURCE ROOT, not in the app's log folder: whoever is looking at a folder
                // that did not fully sort is looking at the folder, not at %LOCALAPPDATA%.
                report.FailureReportPath = TryWrite(fs,
                    Path.Combine(plan.SourceRoot, "failure report.txt"),
                    RenderFailureReport(report),
                    createDirectory: false);
            }

            return report;
        }

        private static string? TryWrite(IFinishFileSystem fs, string path, string contents, bool createDirectory)
        {
            try
            {
                if (createDirectory)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) fs.CreateDirectory(directory);
                }

                fs.WriteAllText(path, contents);
                return path;
            }
            catch (Exception)
            {
                // A log that cannot be written must never turn a completed run into a failed one.
                return null;
            }
        }

        public static string RenderLog(FinishRunReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("FastCull - Finish Session run log");
            sb.AppendLine();
            sb.AppendLine($"Finished    : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Source root : {report.SourceRoot}");
            sb.AppendLine($"Operation   : {report.Operation.ToString().ToUpperInvariant()}");
            sb.AppendLine($"Structure   : {(report.Structure == FinishStructure.Flat ? "FLAT (subfolders discarded)" : "PRESERVED (source layout kept)")}");
            sb.AppendLine($"Outcome     : {report.Outcome.ToString().ToUpperInvariant()}");
            sb.AppendLine($"Elapsed     : {report.Elapsed.TotalSeconds:0.0} s");
            sb.AppendLine($"Written     : {Bytes(report.BytesWritten)}");
            if (report.Message is not null) sb.AppendLine($"Note        : {report.Message}");
            sb.AppendLine();
            sb.AppendLine($"  done          {report.DoneCount,6}");
            sb.AppendLine($"  failed        {report.FailedCount,6}");
            sb.AppendLine($"  not attempted {report.NotAttemptedCount,6}");
            sb.AppendLine($"  renamed       {report.RenamedCount,6}   (collisions avoided)");
            sb.AppendLine($"  left in place {report.Results.Count(r => r.Status == FinishFileStatus.LeftInPlace),6}   (unrated)");
            sb.AppendLine();

            sb.AppendLine("VERIFICATION: every copy was checked by length and SHA-256 of its contents.");
            sb.AppendLine(report.Operation == FinishOperation.Move
                ? "MOVE: each original was deleted only after its copy passed that check."
                : "COPY: all originals were left in place.");
            sb.AppendLine();

            sb.AppendLine("FILES");
            foreach (var r in report.Results.OrderBy(r => r.Status).ThenBy(r => r.Entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                switch (r.Status)
                {
                    case FinishFileStatus.Done:
                        sb.AppendLine($"  [OK]        {r.Entry.RelativePath}  ->  {r.FinalDestination}");
                        if (r.RenamedFrom is not null)
                            sb.AppendLine($"              RENAMED to avoid overwriting {r.RenamedFrom}");
                        break;
                    case FinishFileStatus.LeftInPlace:
                        sb.AppendLine($"  [UNRATED]   {r.Entry.RelativePath}  (left where it is)");
                        break;
                    case FinishFileStatus.Failed:
                        sb.AppendLine($"  [FAILED]    {r.Entry.RelativePath}  -  {r.Error}");
                        break;
                    case FinishFileStatus.NotAttempted:
                        sb.AppendLine($"  [SKIPPED]   {r.Entry.RelativePath}  (run stopped before reaching it; original untouched)");
                        break;
                }
            }

            return sb.ToString();
        }

        public static string RenderFailureReport(FinishRunReport report)
        {
            var sb = new StringBuilder();

            sb.AppendLine("FastCull - Finish Session DID NOT COMPLETE");
            sb.AppendLine();
            sb.AppendLine($"When      : {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Folder    : {report.SourceRoot}");
            sb.AppendLine($"Operation : {report.Operation.ToString().ToUpperInvariant()}");
            sb.AppendLine($"Outcome   : {report.Outcome.ToString().ToUpperInvariant()}");
            if (report.Message is not null) sb.AppendLine($"Reason    : {report.Message}");
            sb.AppendLine();

            sb.AppendLine("WHAT THIS MEANS FOR YOUR PHOTOS");
            sb.AppendLine("  No photo has been lost. Every file below is either still in its original");
            sb.AppendLine("  place, or safely at its destination - never neither.");
            if (report.Operation == FinishOperation.Move)
                sb.AppendLine("  Originals were only deleted after their copy was verified byte for byte.");
            sb.AppendLine();

            var failed = report.Results.Where(r => r.Status == FinishFileStatus.Failed).ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine($"COULD NOT BE PROCESSED ({failed.Count}) - originals still in place:");
                foreach (var r in failed)
                    sb.AppendLine($"  {r.Entry.SourcePath}{Environment.NewLine}      reason: {r.Error}");
                sb.AppendLine();
            }

            var skipped = report.Results.Where(r => r.Status == FinishFileStatus.NotAttempted).ToList();
            if (skipped.Count > 0)
            {
                sb.AppendLine($"NOT REACHED ({skipped.Count}) - originals untouched, nothing was attempted:");
                foreach (var r in skipped) sb.AppendLine($"  {r.Entry.SourcePath}");
                sb.AppendLine();
            }

            var done = report.Results.Where(r => r.Status == FinishFileStatus.Done).ToList();
            if (done.Count > 0)
            {
                sb.AppendLine($"ALREADY COMPLETED ({done.Count}) - these are done and were not rolled back:");
                foreach (var r in done) sb.AppendLine($"  {r.Entry.RelativePath}  ->  {r.FinalDestination}");
                sb.AppendLine();
            }

            sb.AppendLine("Running Finish Session again will retry whatever is still here.");

            return sb.ToString();
        }
    }
}
