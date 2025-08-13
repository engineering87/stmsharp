// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.PerformanceTests.Benchmarks
{
    [MemoryDiagnoser]
    [RankColumn]
    public class STMPerformanceBenchmark
    {
        private STMVariable<int> _variable;

        [Params(BackoffType.Exponential, BackoffType.ExponentialWithJitter, BackoffType.Linear, BackoffType.Constant)]
        public BackoffType Backoff;

        [GlobalSetup]
        public void Setup()
        {
            _variable = new STMVariable<int>(0);
        }

        [Benchmark(Baseline = true)]
        public int WriteVariable()
        {
            _variable.Write(_variable.Read() + 1);
            return _variable.Read();
        }

        [Benchmark]
        public int ReadVariable()
        {
            return _variable.Read();
        }

        [Benchmark]
        public async Task AtomicWrite()
        {
            await STMEngine.Atomic<int>(transaction =>
            {
                var value = transaction.Read(_variable);
                transaction.Write(_variable, value + 1);
            }, maxAttempts: 3, initialBackoffMilliseconds: 50, backoffType: Backoff);
        }

        [Benchmark]
        public async Task AtomicReadOnly()
        {
            await STMEngine.Atomic<int>(transaction =>
            {
                var value = transaction.Read(_variable);
            }, maxAttempts: 3, initialBackoffMilliseconds: 50, backoffType: Backoff, readOnly: true);
        }
    }
}
