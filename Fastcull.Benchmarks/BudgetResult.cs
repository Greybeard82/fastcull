using System;
using System.Collections.Generic;
using System.Globalization;

namespace Fastcull.Benchmarks
{
    /// <summary>Why a PRD 3.5 metric produced no number.</summary>
    internal enum Unmeasurable
    {
        /// <summary>It was measured.</summary>
        No,

        /// <summary>The feature it measures does not exist yet, so there is nothing to time.</summary>
        FeatureNotBuilt,
    }

    /// <summary>
    /// One row of PRD 3.5's budget table. A row that could not be honestly measured is still a
    /// row - it carries its reason instead of a number, so the results file reads as a complete
    /// picture rather than a partial one dressed up as complete.
    /// </summary>
    internal sealed record BudgetResult
    {
        public required string Metric { get; init; }
        public required string Budget { get; init; }

        /// <summary>Null when <see cref="Unmeasurable"/> is not <see cref="Unmeasurable.No"/>.</summary>
        public double? MeasuredMs { get; init; }

        /// <summary>Budget in the same unit as <see cref="MeasuredMs"/>, for the pass/fail test.</summary>
        public double? BudgetMs { get; init; }

        public Unmeasurable Unmeasurable { get; init; } = Unmeasurable.No;
        public string? Reason { get; init; }
        public string? Detail { get; init; }

        /// <summary>True only for a row that was measured AND is inside budget.</summary>
        public bool? Passed => Unmeasurable != Unmeasurable.No || MeasuredMs is null || BudgetMs is null
            ? null
            : MeasuredMs <= BudgetMs;

        public string StatusText => Unmeasurable switch
        {
            Unmeasurable.FeatureNotBuilt => "NOT MEASURABLE",
            _ => Passed switch
            {
                true => "PASS",
                false => "**FAIL**",
                _ => "-",
            },
        };

        public string MeasuredText => MeasuredMs switch
        {
            null => "-",
            // Sub-millisecond values render as microseconds; "0.0 ms" reads as "not measured"
            // when it actually means "far inside budget".
            < 1 and var ms => string.Create(CultureInfo.InvariantCulture, $"{ms * 1000:F1} µs"),
            >= 1000 and var ms => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:F2} s"),
            var ms => string.Create(CultureInfo.InvariantCulture, $"{ms:F1} ms"),
        };

        public static BudgetResult NotBuilt(string metric, string budget, string reason)
            => new() { Metric = metric, Budget = budget, Unmeasurable = Unmeasurable.FeatureNotBuilt, Reason = reason };
    }

    /// <summary>A non-time measurement (memory), kept separate so it never lands in a ms column.</summary>
    internal sealed record MemoryResult
    {
        public required string Metric { get; init; }
        public required string Budget { get; init; }
        public required double MeasuredMb { get; init; }
        public required double BudgetMb { get; init; }
        public string? Detail { get; init; }

        public bool Passed => MeasuredMb <= BudgetMb;
        public string StatusText => Passed ? "PASS" : "**FAIL**";
    }

    internal sealed class HarnessResults
    {
        public List<BudgetResult> Budgets { get; } = new();
        public List<MemoryResult> Memory { get; } = new();
        public List<string> Notes { get; } = new();
        public Dictionary<string, string> Environment { get; } = new();
    }
}
