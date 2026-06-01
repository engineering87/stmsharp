// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Configs;

namespace STMSharp.PerformanceTests.Benchmarks
{
    /// <summary>
    /// Benchmark configuration that reproduces the default BenchmarkDotNet behavior with one
    /// deliberate exception: the <see cref="MinIterationTimeAnalyser"/> is omitted.
    ///
    /// Rationale
    /// ---------
    /// Every operation under test runs in nanoseconds to a few microseconds. Each benchmark already
    /// amortizes the timer resolution through an OperationsPerInvoke inner loop (see STMPerformanceBenchmark),
    /// so the reported per-operation Mean is precise and stable (error margins under three percent, unimodal
    /// distributions). The single iteration time, however, stays in the low-millisecond range, below the
    /// 100ms threshold that MinIterationTimeAnalyser recommends. That analyser is a heuristic: it flags the
    /// iteration time, not the statistical quality of the result. Raising the operation counts to clear 100ms
    /// would multiply the suite runtime several fold without improving the published numbers, which already
    /// rest on tens of thousands of samples per iteration. The warning is therefore suppressed here as a
    /// conscious, documented choice rather than padded away.
    ///
    /// BenchmarkDotNet exposes no public API to remove a single default analyser from a config, so the
    /// configuration is rebuilt by copying the default components and re-adding the default analysers minus
    /// the one we drop. Jobs, diagnosers, ordering, ranking, and hidden columns continue to come from the
    /// attributes on the benchmark class and union with this config; they are intentionally not set here.
    /// </summary>
    public sealed class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            var defaultConfig = DefaultConfig.Instance;

            // ManualConfig starts empty, so the default loggers, exporters, column providers, and validators
            // must be carried over or the console output and summary table would be lost.
            AddLogger(defaultConfig.GetLoggers().ToArray());
            AddExporter(defaultConfig.GetExporters().ToArray());
            AddColumnProvider(defaultConfig.GetColumnProviders().ToArray());
            AddValidator(defaultConfig.GetValidators().ToArray());

            // Re-add every default analyser except MinIterationTimeAnalyser.
            AddAnalyser(defaultConfig.GetAnalysers()
                .Where(analyser => analyser is not MinIterationTimeAnalyser)
                .ToArray());
        }
    }
}