using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Fastcull.Models;

namespace Fastcull.Services
{
    /// <summary>Which pile a photo lands in (PRD 4.3).</summary>
    public enum FinishBucket
    {
        /// <summary>Unflagged - stays exactly where it is.</summary>
        Untouched,
        Approved,
        Rejected,
        Rated,
    }

    /// <summary>Whether the batch moves files or leaves the originals in place (PRD 4.2).</summary>
    public enum FinishOperation
    {
        /// <summary>Nothing chosen yet. Confirm stays disabled while the choice is this.</summary>
        None,
        Copy,
        Move,
    }

    /// <summary>
    /// Whether the bucket keeps the source's subfolder layout (PRD 4.2.2).
    ///
    /// <see cref="Flat"/> makes name collisions markedly more likely rather than merely possible:
    /// preserving the layout means two cards' DSC_0001.ARW land in different subfolders and never
    /// meet, while flattening puts them in the same directory by design. That does not make it
    /// unsafe - the collision-safe rename and CreateNew in <see cref="FinishExecutor"/> are what
    /// guarantee nothing is overwritten, and they are indexed on the destination path, so they
    /// apply here unchanged. It does make the renames common instead of rare, which is why every
    /// one of them is written to the run log.
    /// </summary>
    public enum FinishStructure
    {
        /// <summary>Default. The path relative to the scan root is preserved inside the bucket.</summary>
        Preserve,

        /// <summary>Every photo lands directly in the bucket root; source subfolders are discarded.</summary>
        Flat,
    }

    /// <summary>One photo's fate.</summary>
    /// <param name="SourcePath">Absolute path today.</param>
    /// <param name="RelativePath">Path relative to the scan root, preserved inside the bucket.</param>
    /// <param name="Bucket">Which pile.</param>
    /// <param name="Stars">1-5 for <see cref="FinishBucket.Rated"/>, otherwise 0.</param>
    /// <param name="DestinationPath">Absolute destination, or null when untouched.</param>
    public readonly record struct FinishPlanEntry(
        string SourcePath,
        string RelativePath,
        FinishBucket Bucket,
        int Stars,
        string? DestinationPath);

    /// <summary>
    /// What a Finish Session would do, computed but not performed.
    /// </summary>
    public sealed class FinishPlan
    {
        public required string SourceRoot { get; init; }
        public required FinishOperation Operation { get; init; }
        public required IReadOnlyList<FinishPlanEntry> Entries { get; init; }

        /// <summary>Whether destinations preserve the source layout or flatten it (PRD 4.2.2).</summary>
        public FinishStructure Structure { get; init; } = FinishStructure.Preserve;

        /// <summary>Everything that would actually be written somewhere - untouched excluded.</summary>
        public IEnumerable<FinishPlanEntry> Moves => Entries.Where(e => e.Bucket != FinishBucket.Untouched);

        public int Total => Entries.Count;
        public int ApprovedCount => Entries.Count(e => e.Bucket == FinishBucket.Approved);
        public int RejectedCount => Entries.Count(e => e.Bucket == FinishBucket.Rejected);
        public int UntouchedCount => Entries.Count(e => e.Bucket == FinishBucket.Untouched);
        public int StarCount(int stars) => Entries.Count(e => e.Bucket == FinishBucket.Rated && e.Stars == stars);

        /// <summary>How many files the operation would actually touch.</summary>
        public int AffectedCount => Total - UntouchedCount;
    }

    /// <summary>
    /// Turns cull states into destinations, per PRD 4.3.
    ///
    /// Pure, WinUI-free and in Core so the bucketing can be tested headlessly - which matters more
    /// here than almost anywhere else in the app, because the thing being decided is where
    /// somebody's photographs end up. Stage 1 only plans; nothing in this file opens, creates,
    /// copies, moves or deletes a file.
    /// </summary>
    public static class FinishPlanner
    {
        public const string ApprovedFolder = "Approved";
        public const string RejectedFolder = "Rejected";
        public const string RatedFolder = "Rated";

        /// <summary>
        /// Whether a path relative to the scan root is inside one of the output buckets.
        ///
        /// **The scan must skip these.** Sorting happens in place under the source root (§4.3), so
        /// the output lands inside the folder that gets rescanned - and without this filter a
        /// reopened session lists every photo twice, once where it started and once where it was
        /// sorted to. Measured before this existed: a 200-photo folder reopened as 400. Worse, a
        /// second Finish Session would then re-sort the already-sorted copies into Approved/Approved
        /// and, in Move mode, relocate them again.
        ///
        /// This is what makes §4.1's "a reopened session is what is still here" mean what it says:
        /// the photos still awaiting a decision, not the ones already dealt with.
        /// </summary>
        public static bool IsInsideBucket(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;

            var first = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            return first is not null
                && (first.Equals(ApprovedFolder, StringComparison.OrdinalIgnoreCase)
                    || first.Equals(RejectedFolder, StringComparison.OrdinalIgnoreCase)
                    || first.Equals(RatedFolder, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Which pile a state belongs to.
        ///
        /// **Stars are tested before the flag, and that order is the rule** (PRD 4.3). Section 1.6's
        /// ladder makes every starred photo Picked as well, so a flag-first test would route all of
        /// them to Approved and leave the star folders permanently empty. A 3-star photo belongs in
        /// Rated/3 and nowhere else.
        /// </summary>
        public static FinishBucket BucketFor(CullState state) => state switch
        {
            { Stars: >= 1 } => FinishBucket.Rated,
            { Flag: Flag.Rejected } => FinishBucket.Rejected,
            { Flag: Flag.Picked } => FinishBucket.Approved,
            _ => FinishBucket.Untouched,
        };

        /// <summary>
        /// The bucket's folder relative to the scan root, or null for untouched.
        /// </summary>
        public static string? BucketFolder(FinishBucket bucket, int stars) => bucket switch
        {
            FinishBucket.Approved => ApprovedFolder,
            FinishBucket.Rejected => RejectedFolder,
            FinishBucket.Rated => Path.Combine(RatedFolder, stars.ToString()),
            _ => null,
        };

        /// <summary>
        /// Builds the plan. <paramref name="photos"/> supplies each photo's absolute path, its path
        /// relative to the scan root, and its cull state.
        ///
        /// <paramref name="structure"/> decides the shape inside the bucket (PRD 4.2.2). Preserving
        /// the relative path is the default and the safer of the two: two cards can both hold
        /// DSC_0001.ARW, and under Preserve they land in different subfolders and never meet.
        /// Flattening deliberately puts them in one directory, where the executor's collision-safe
        /// rename is what keeps the second from landing on the first. Neither mode ever overwrites;
        /// Flat simply exercises the rename far more often.
        ///
        /// Defaulted to Preserve so existing callers keep the behaviour they were written against.
        /// </summary>
        public static FinishPlan Plan(
            string sourceRoot,
            FinishOperation operation,
            IEnumerable<(string AbsolutePath, string RelativePath, CullState State)> photos,
            FinishStructure structure = FinishStructure.Preserve)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
            ArgumentNullException.ThrowIfNull(photos);

            var entries = new List<FinishPlanEntry>();

            foreach (var (absolute, relative, state) in photos)
            {
                var bucket = BucketFor(state);
                var stars = bucket == FinishBucket.Rated ? state.Stars : 0;
                var folder = BucketFolder(bucket, stars);

                // Flat keeps the file name and discards everything above it. GetFileName copes with
                // either separator, so a relative path that arrived with forward slashes flattens
                // the same as one that did not.
                var withinBucket = structure == FinishStructure.Flat
                    ? Path.GetFileName(relative)
                    : relative;

                entries.Add(new FinishPlanEntry(
                    absolute,
                    relative,
                    bucket,
                    stars,
                    folder is null ? null : Path.Combine(sourceRoot, folder, withinBucket)));
            }

            return new FinishPlan
            {
                SourceRoot = sourceRoot,
                Operation = operation,
                Structure = structure,
                Entries = entries,
            };
        }

        /// <summary>
        /// Renders the plan as the dry-run log of PRD 4.2.1: a header, a per-bucket tally, then
        /// every file with the destination it would have been given.
        ///
        /// Untouched photos are listed too, marked as such. Leaving them out would make the log
        /// unable to answer "why was this one not sorted?", which is exactly the question a dry run
        /// exists to answer.
        /// </summary>
        public static string Render(FinishPlan plan, DateTimeOffset timestamp)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var sb = new StringBuilder();

            sb.AppendLine("FastCull - Finish Session PLAN (dry run, PRD 4.2.1)");
            sb.AppendLine("NO FILES WERE MOVED OR COPIED. This is stage 1: planning only.");
            sb.AppendLine();
            sb.AppendLine($"Generated   : {timestamp:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Source root : {plan.SourceRoot}");
            sb.AppendLine($"Operation   : {plan.Operation.ToString().ToUpperInvariant()}");
            sb.AppendLine($"Structure   : {(plan.Structure == FinishStructure.Flat ? "FLAT (subfolders discarded)" : "PRESERVED (source layout kept)")}");
            sb.AppendLine();

            sb.AppendLine("SUMMARY");
            sb.AppendLine($"  Total photos     {plan.Total,6}");
            sb.AppendLine($"  Approved         {plan.ApprovedCount,6}   -> {ApprovedFolder}");
            sb.AppendLine($"  Rejected         {plan.RejectedCount,6}   -> {RejectedFolder}");

            for (var stars = 1; stars <= 5; stars++)
                sb.AppendLine($"  {stars} star{(stars == 1 ? " " : "s")}          {plan.StarCount(stars),6}   -> {Path.Combine(RatedFolder, stars.ToString())}");

            sb.AppendLine($"  Unrated          {plan.UntouchedCount,6}   -> (untouched, stays in place)");
            sb.AppendLine();
            sb.AppendLine($"  Files affected   {plan.AffectedCount,6}");
            sb.AppendLine();

            sb.AppendLine("PLAN");
            foreach (var entry in plan.Entries.OrderBy(e => e.Bucket).ThenBy(e => e.Stars).ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(entry.Bucket == FinishBucket.Untouched
                    ? $"  [UNTOUCHED] {entry.RelativePath}"
                    : $"  [{Label(entry),-11}] {entry.RelativePath}  ->  {entry.DestinationPath}");
            }

            return sb.ToString();
        }

        private static string Label(FinishPlanEntry entry) => entry.Bucket switch
        {
            FinishBucket.Rated => $"RATED {entry.Stars}",
            FinishBucket.Approved => "APPROVED",
            FinishBucket.Rejected => "REJECTED",
            _ => "UNTOUCHED",
        };

        /// <summary>Where dry-run logs go (PRD 4.4 already names this directory).</summary>
        public static string LogDirectory => Path.Combine(AppSettings.RootDirectory, "logs");

        /// <summary>
        /// Writes the rendered plan and returns its path, or null if it could not be written.
        ///
        /// Never throws: a log that cannot be written must not look like a failed Finish Session.
        /// Call this off the UI thread - it is the only file I/O in the flow, and CLAUDE.md's
        /// constraint has no exception for "only a small write".
        /// </summary>
        public static string? WriteLog(FinishPlan plan, DateTimeOffset timestamp)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);

                var path = Path.Combine(LogDirectory, $"finish-plan-{timestamp:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(path, Render(plan, timestamp));
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
