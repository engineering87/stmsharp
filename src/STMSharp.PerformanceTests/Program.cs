// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)

using System;
using System.Collections.Generic;
using System.Linq;
using BenchmarkDotNet.Running;
using STMSharp.PerformanceTests.Benchmarks;

namespace STMSharp.PerformanceTests
{
    public class Program
    {
        // Accumulates mean times (ns) for AtomicWrite/AtomicReadOnly grouped by Backoff
        private static readonly Dictionary<string, (double? write, double? read)> _backoffTable = new();

        public static void Main(string[] args)
        {
            // Run multiple benchmark classes in a single process invocation.
            // Add or remove types here as your suite evolves.
            var summaries = BenchmarkSwitcher.FromTypes(new[]
            {
                typeof(STMPerformanceBenchmark),
            }).Run(args);

            // Aggregate results across all summaries
            foreach (var summary in summaries)
            {
                foreach (var report in summary.Reports)
                {
                    // Skip if no stats (e.g., failed run)
                    var stats = report.ResultStatistics;
                    if (stats is null) continue;

                    var methodName = report.BenchmarkCase.Descriptor.WorkloadMethod?.Name ?? string.Empty;

                    // We only aggregate entries that expose a "Backoff" parameter
                    var backoffParam = report.BenchmarkCase.Parameters.Items
                        .FirstOrDefault(p => string.Equals(p.Name, "Backoff", StringComparison.Ordinal));

                    if (backoffParam is null)
                        continue;

                    var backoffKey = backoffParam.Value?.ToString() ?? "(null)";

                    // Track only AtomicWrite / AtomicReadOnly to keep the table focused
                    if (!string.Equals(methodName, "AtomicWrite", StringComparison.Ordinal) &&
                        !string.Equals(methodName, "AtomicReadOnly", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!_backoffTable.TryGetValue(backoffKey, out var entry))
                        entry = (null, null);

                    var meanNs = stats.Mean; // BenchmarkDotNet reports means in nanoseconds

                    if (string.Equals(methodName, "AtomicWrite", StringComparison.Ordinal))
                        entry = (meanNs, entry.read);
                    else // AtomicReadOnly
                        entry = (entry.write, meanNs);

                    _backoffTable[backoffKey] = entry;
                }
            }

            // Pretty-print table (sorted by AtomicWrite mean when available)
            Console.WriteLine();
            Console.WriteLine("Average Time Summary for Atomic Operations by Backoff Type");
            Console.WriteLine("| Backoff Type               | AtomicWrite (ns) | AtomicReadOnly (ns) |");
            Console.WriteLine("|----------------------------|-----------------:|--------------------:|");

            var sorted = _backoffTable
                .OrderBy(kvp => kvp.Value.write ?? double.MaxValue)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal);

            foreach (var kvp in sorted)
            {
                var key = kvp.Key.PadRight(26);
                var write = kvp.Value.write.HasValue ? kvp.Value.write.Value.ToString("F2") : "n/a";
                var read = kvp.Value.read.HasValue ? kvp.Value.read.Value.ToString("F2") : "n/a";
                Console.WriteLine($"| {key} | {write,16} | {read,19} |");
            }
        }
    }
}