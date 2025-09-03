// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)

using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.PerformanceTests.Benchmarks
{
    /// <summary>
    /// Simple end-to-end STM benchmarks used by Program.cs to summarize mean times by Backoff type.
    /// Notes:
    /// - Job runtime aligned to .NET 9 (Net90) to match the project TFM (net9.0).
    /// - State is reset per iteration to avoid cross-iteration skew.
    /// - Keep method names 'AtomicWrite' and 'AtomicReadOnly' for Program.cs aggregation.
    /// </summary>
    [MemoryDiagnoser]
    [RankColumn]
    [Orderer(SummaryOrderPolicy.FastestToSlowest)]
    [HideColumns("StdDev")] // modern BDN: hide by column name
    [SimpleJob(RuntimeMoniker.Net90, launchCount: 1, warmupCount: 3, iterationCount: 10)]
    public class STMPerformanceBenchmark
    {
        private STMVariable<int> _variable = default!;

        // Backoff type is the key used by Program.cs to group results
        [Params(BackoffType.Exponential, BackoffType.ExponentialWithJitter, BackoffType.Linear, BackoffType.Constant)]
        public BackoffType Backoff { get; set; }

        // Small knobs to make runs comparable and reproducible
        [Params(16)]
        public int MaxAttempts { get; set; }

        [Params(2)]
        public int InitialBackoffMilliseconds { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            _variable = new STMVariable<int>(0);
        }

        [IterationSetup]
        public void IterationSetup()
        {
            // Ensure each iteration starts from a clean state
            _variable = new STMVariable<int>(0);
        }

        // ---------------- Baselines (non-transactional) ----------------

        [Benchmark(Baseline = true)]
        public int WriteVariable()
        {
            // direct non-transactional write (for reference)
            _variable.Write(_variable.Read() + 1);
            return _variable.Read();
        }

        [Benchmark]
        public int ReadVariable()
        {
            // direct non-transactional read
            return _variable.Read();
        }

        // ---------------- Transactional ----------------

        [Benchmark]
        public async Task AtomicWrite()
        {
            await STMEngine.Atomic<int>(tx =>
            {
                var value = tx.Read(_variable);
                tx.Write(_variable, value + 1);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: false,
            cancellationToken: CancellationToken.None);
        }

        [Benchmark]
        public async Task AtomicReadOnly()
        {
            await STMEngine.Atomic<int>(tx =>
            {
                var _ = tx.Read(_variable);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: true,
            cancellationToken: CancellationToken.None);
        }
    }
}