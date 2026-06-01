// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Linq;
using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;

namespace STMSharp.PerformanceTests.Benchmarks
{
    /// <summary>
    /// Single authoritative benchmark configuration. It reproduces the default BenchmarkDotNet
    /// behavior with one deliberate exception: the <see cref="MinIterationTimeAnalyser"/> is omitted.
    ///
    /// Why this is a full config rather than a few attributes
    /// ------------------------------------------------------
    /// BenchmarkDotNet exposes no public API to remove a single default analyser. Filtering it out of a
    /// config has no effect under the default ConfigUnionRule.Union, because the global default config is
    /// then merged back in and re-adds the analyser (and duplicates the exporters). To make the exclusion
    /// stick, this config sets UnionRule = ConfigUnionRule.AlwaysUseLocal, which means the default config is
    /// not merged at all. That in turn requires every element to be specified here: loggers, exporters,
    /// column providers, validators, analysers, the diagnoser, the job, the orderer, the rank column, and
    /// the hidden column. The benchmark class therefore carries only [Config(typeof(BenchmarkConfig))].
    ///
    /// Why the analyser is dropped
    /// ---------------------------
    /// Every operation under test runs in nanoseconds to a few microseconds. Each benchmark amortizes the
    /// timer resolution through an OperationsPerInvoke inner loop (see STMPerformanceBenchmark), so the
    /// reported per-operation Mean is precise and stable (error margins under three percent, unimodal
    /// distributions, tens of thousands of samples per iteration). The single iteration time stays in the
    /// low-millisecond range, below the 100ms that MinIterationTimeAnalyser recommends. That analyser flags
    /// the iteration time, not the statistical quality of the result, so it is suppressed here as a
    /// conscious, documented choice rather than padded away with inflated operation counts that would
    /// multiply the suite runtime several fold without improving the published numbers.
    /// </summary>
    public sealed class BenchmarkConfig : ManualConfig
    {
        public BenchmarkConfig()
        {
            var defaultConfig = DefaultConfig.Instance;

            // Carry over the default presentation and validation pipeline. ManualConfig starts empty, so
            // without this there would be no console output, no summary table, and no JIT-optimization check.
            AddLogger(defaultConfig.GetLoggers().ToArray());
            AddExporter(defaultConfig.GetExporters().ToArray());
            AddColumnProvider(defaultConfig.GetColumnProviders().ToArray());
            AddValidator(defaultConfig.GetValidators().ToArray());

            // Every default analyser except MinIterationTimeAnalyser.
            AddAnalyser(defaultConfig.GetAnalysers()
                .Where(analyser => analyser is not MinIterationTimeAnalyser)
                .ToArray());

            // Memory column (Gen0, Allocated). Previously the [MemoryDiagnoser] attribute.
            AddDiagnoser(MemoryDiagnoser.Default);

            // Job aligned to the project TFM (net10.0). Previously the [SimpleJob] attribute. InvocationCount
            // and UnrollFactor are pinned to 1 automatically by BenchmarkDotNet because the benchmark uses
            // [IterationSetup]; they are not set here.
            AddJob(Job.Default
                .WithRuntime(CoreRuntime.Core10_0)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10));

            // Presentation: fastest to slowest, ranked, with StdDev hidden. Previously the [Orderer],
            // [RankColumn], and [HideColumns] attributes.
            Orderer = new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest);
            AddColumn(RankColumn.Arabic);
            HideColumns("StdDev");

            // Use this config alone; do not merge the global default config back in.
            UnionRule = ConfigUnionRule.AlwaysUseLocal;
        }
    }
}