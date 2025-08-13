// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Running;
using STMSharp.PerformanceTests.Benchmarks;

namespace STMSharp.PerformanceTests
{
    public class Program
    {
        private static readonly Dictionary<object, (double write, double read)> _backoffTable = new();

        public static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<STMPerformanceBenchmark>();

            Console.WriteLine("\nAverage Time Summary for Atomic Operations by Backoff Type:");
            Console.WriteLine("| Backoff Type         | AtomicWrite (ns) | AtomicReadOnly (ns) |");
            Console.WriteLine("|-------------------- |----------------:|-------------------:|");

            foreach (var report in summary.Reports)
            {
                var methodName = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
                if (methodName != "AtomicWrite" && methodName != "AtomicReadOnly")
                    continue;

                var backoff = report.BenchmarkCase.Parameters["Backoff"];
                var mean = report.ResultStatistics.Mean;

                if (!_backoffTable.ContainsKey(backoff))
                    _backoffTable[backoff] = (0, 0);

                var entry = _backoffTable[backoff];
                if (methodName == "AtomicWrite")
                    _backoffTable[backoff] = (mean, entry.read);
                else
                    _backoffTable[backoff] = (entry.write, mean);
            }

            var sorted = _backoffTable.OrderBy(kvp => kvp.Value.write);

            foreach (var kvp in sorted)
            {
                Console.WriteLine($"| {kvp.Key,-18} | {kvp.Value.write,16:F2} | {kvp.Value.read,18:F2} |");
            }
        }
    }
}