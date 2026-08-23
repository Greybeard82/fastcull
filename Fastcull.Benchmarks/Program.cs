using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Fastcull.Benchmarks
{
    /// <summary>
    /// Entry point for the PRD 3.5 perf harness.
    ///
    ///   dotnet run -c Release --project Fastcull.Benchmarks
    ///   dotnet run -c Release --project Fastcull.Benchmarks -- --corpus &lt;path&gt; --out &lt;file.md&gt; --title "..."
    ///
    /// Run in Release. A Debug run measures unoptimised code and its numbers are not comparable
    /// to the committed baseline; the harness says so loudly rather than quietly reporting them.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                var repoRoot = FindRepoRoot();
                var corpus = ArgValue(args, "--corpus") ?? Path.Combine(repoRoot, "SampleImages");
                var outPath = ArgValue(args, "--out")
                    ?? Path.Combine(repoRoot, "docs", "benchmarks", "2026-08-23-baseline.md");

                if (!Directory.Exists(corpus))
                {
                    Console.Error.WriteLine($"Corpus not found: {corpus}");
                    return 2;
                }

#if DEBUG
                Console.WriteLine("WARNING: Debug build. These numbers measure unoptimised code and are not");
                Console.WriteLine("         comparable to the committed baseline. Use -c Release.");
                Console.WriteLine();
#endif

                var syntheticRoot = Path.Combine(repoRoot, "Fastcull.Benchmarks", "synthetic-corpus");

                Console.WriteLine($"Real corpus:      {corpus}");
                Console.Write($"Synthetic corpus: {syntheticRoot} ... ");
                var synthetic = SyntheticCorpus.Build(corpus, syntheticRoot, SyntheticCorpus.TargetFileCount);
                var logicalGb = SyntheticCorpus.LogicalBytes(syntheticRoot) / 1024.0 / 1024 / 1024;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{synthetic.FileCount:N0} hard links over {synthetic.DistinctSourceCount} distinct files "
                    + $"({logicalGb:F1} GB logical, ~0 GB on disk)"));
                Console.WriteLine();

                Console.WriteLine("Running harness (this takes a few minutes - it decodes the whole corpus twice)...");
                Console.WriteLine();

                var harness = new PerfHarness(corpus, syntheticRoot);
                var results = await harness.RunAsync().ConfigureAwait(false);

                results.Environment["Real corpus files"] = CountFiles(corpus).ToString(CultureInfo.InvariantCulture);
                results.Environment["Synthetic corpus files"] =
                    string.Create(CultureInfo.InvariantCulture,
                        $"{synthetic.FileCount:N0} (hard links over {synthetic.DistinctSourceCount} distinct files)");

                var title = ArgValue(args, "--title") ?? "FastCull performance baseline — 2026-08-23";
                var markdown = MarkdownReport.Render(
                    results, DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture), title);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, markdown);

                PrintConsoleSummary(results);
                Console.WriteLine();
                Console.WriteLine($"Wrote {outPath}");

                // Exit code reflects only rows that were measured AND failed. NOT MEASURABLE never
                // fails the run - it is a gap in the app, not a regression in it.
                var failures = results.Budgets.Count(b => b.Passed == false) + results.Memory.Count(m => !m.Passed);
                return failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 3;
            }
        }

        private static void PrintConsoleSummary(HarnessResults results)
        {
            Console.WriteLine();
            foreach (var b in results.Budgets)
            {
                var status = b.StatusText.Replace("**", string.Empty);
                Console.WriteLine($"  [{status,-14}] {b.Metric,-62} {b.MeasuredText,10}  (budget {b.Budget})");
            }
            foreach (var m in results.Memory)
            {
                var status = m.StatusText.Replace("**", string.Empty);
                var value = string.Create(CultureInfo.InvariantCulture, $"{m.MeasuredMb / 1024:F2} GB");
                Console.WriteLine($"  [{status,-14}] {m.Metric,-62} {value,10}  (budget {m.Budget})");
            }
        }

        private static int CountFiles(string root)
            => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(FixtureSets.IsSupported);

        private static string? ArgValue(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Fastcull.sln")))
                    return dir.FullName;
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
