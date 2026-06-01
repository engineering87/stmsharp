// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using STMSharp.Core;
using STMSharp.Core.Collections;
using STMSharp.Enum;

namespace STMSharp.PerformanceTests.Benchmarks
{
    /// <summary>
    /// Simple end-to-end STM benchmarks used by Program.cs to summarize mean times by Backoff type.
    /// Notes:
    /// - Job runtime aligned to .NET 10 (Net100) to match the project TFM (net10.0).
    /// - State is reset per iteration to avoid cross-iteration skew.
    /// - Keep method names 'AtomicWrite' and 'AtomicReadOnly' for Program.cs aggregation.
    /// </summary>
    [MemoryDiagnoser]
    [RankColumn]
    [Orderer(SummaryOrderPolicy.FastestToSlowest)]
    [HideColumns("StdDev")] // modern BDN: hide by column name
    [SimpleJob(RuntimeMoniker.Net10_0, launchCount: 1, warmupCount: 3, iterationCount: 10)]
    public class STMPerformanceBenchmark
    {
        private STMVariable<int> _variable = default!;

        // Transactional dictionary state for the collection benchmarks. The dictionary is seeded
        // with SeedCount existing keys so that the structural insert path copies a non-trivial
        // directory and the cost of copy-on-write is visible, not hidden by an empty map.
        private const int SeedCount = 16;
        private const int ExistingKey = 0;
        private const int AbsentKey = SeedCount + 1;
        private TransactionalDictionary<int, int> _dict = default!;

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
            ResetDictionary();
        }

        [IterationSetup]
        public void IterationSetup()
        {
            // Ensure each iteration starts from a clean state
            _variable = new STMVariable<int>(0);
            ResetDictionary();
        }

        // Rebuilds the dictionary and seeds SeedCount keys in a single transaction. Runs in setup,
        // so it is not part of any measured result. AbsentKey is deliberately left out so that the
        // insert benchmark always exercises the structural path.
        private void ResetDictionary()
        {
            _dict = new TransactionalDictionary<int, int>();
            STMEngine.Atomic(tx =>
            {
                for (int k = 0; k < SeedCount; k++)
                {
                    _dict.Set(tx, k, 0);
                }
            }).GetAwaiter().GetResult();
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

        // ---- Non-generic API (no per-call LegacyTransactionView adapter allocation) ----

        [Benchmark]
        public async Task AtomicWriteNonGeneric()
        {
            await STMEngine.Atomic(tx =>
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
        public async Task AtomicReadOnlyNonGeneric()
        {
            await STMEngine.Atomic(tx =>
            {
                var _ = tx.Read(_variable);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: true,
            cancellationToken: CancellationToken.None);
        }

        // ---- Contended write (exercises the retry path and its per-attempt allocations) ----

        [Benchmark]
        public async Task AtomicWriteContended()
        {
            const int writers = 8;
            var shared = _variable;
            var tasks = new Task[writers];

            for (int i = 0; i < writers; i++)
            {
                tasks[i] = Task.Run(() => STMEngine.Atomic(tx =>
                {
                    var value = tx.Read(shared);
                    tx.Write(shared, value + 1);
                },
                maxAttempts: 64, // generous so contention does not exhaust the budget during measurement
                initialBackoffMilliseconds: InitialBackoffMilliseconds,
                backoffType: Backoff,
                cancellationToken: CancellationToken.None));
            }

            await Task.WhenAll(tasks);
        }

        // ---- Transactional dictionary (fine-grained values, coarse structure) ----

        [Benchmark]
        public async Task DictionarySetExisting()
        {
            // Fine-grained value update on an existing key: writes only that key's value cell,
            // no structural change to the directory.
            await STMEngine.Atomic(tx =>
            {
                var value = _dict.Get(tx, ExistingKey);
                _dict.Set(tx, ExistingKey, value + 1);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: false,
            cancellationToken: CancellationToken.None);
        }

        [Benchmark]
        public async Task DictionarySetNew()
        {
            // Structural insert: copies the directory snapshot (copy-on-write over SeedCount keys)
            // and publishes a new one. AbsentKey is reset to absent before each measured iteration.
            await STMEngine.Atomic(tx =>
            {
                _dict.Set(tx, AbsentKey, 1);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: false,
            cancellationToken: CancellationToken.None);
        }

        [Benchmark]
        public async Task DictionaryTryGet()
        {
            // Read path: reads the directory snapshot and the key's value cell.
            await STMEngine.Atomic(tx =>
            {
                _dict.TryGetValue(tx, ExistingKey, out _);
            },
            maxAttempts: MaxAttempts,
            initialBackoffMilliseconds: InitialBackoffMilliseconds,
            backoffType: Backoff,
            readOnly: true,
            cancellationToken: CancellationToken.None);
        }
    }
}