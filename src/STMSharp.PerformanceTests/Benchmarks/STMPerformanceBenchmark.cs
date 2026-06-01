// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
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
    /// - Job runtime aligned to .NET 10 (Net10_0) to match the project TFM (net10.0).
    /// - State is reset per iteration to avoid cross-iteration skew.
    /// - Keep method names 'AtomicWrite' and 'AtomicReadOnly' for Program.cs aggregation.
    ///
    /// Measurement floor and OperationsPerInvoke
    /// ---------------------------------------------
    /// Each operation here runs in nanoseconds to a few microseconds. With [IterationSetup] present
    /// BenchmarkDotNet pins InvocationCount to 1, so a single operation per iteration falls below the
    /// timer resolution and yields pure noise. To make the per-operation numbers trustworthy, every
    /// benchmark runs an inner loop of OperationsPerInvoke operations: BenchmarkDotNet divides the measured
    /// iteration time by that count, so the reported Mean is the per-operation cost (and the Program.cs
    /// aggregation, which reads ResultStatistics.Mean, is unaffected). With this loop the per-operation
    /// results are precise and stable: error margins under three percent and unimodal distributions.
    ///
    /// The per-method counts below are kept modest so the full suite runs in tens of seconds. They keep
    /// each iteration in the low-millisecond range, which is below the 100ms that MinIterationTimeAnalyser
    /// recommends but already contains tens of thousands of samples per iteration. The analyser flags the
    /// iteration time, not the statistical quality, so it is suppressed by BenchmarkConfig (see
    /// [Config] below) as a conscious, documented choice rather than padded away with inflated counts.
    /// The counts are deliberately tier-grouped rather than fitted per method, since OperationsPerInvoke
    /// only changes how long an iteration runs, not the per-operation Mean it reports.
    /// </summary>
    [Config(typeof(BenchmarkConfig))] // default BDN behavior minus the MinIterationTime warning
    [MemoryDiagnoser]
    [RankColumn]
    [Orderer(SummaryOrderPolicy.FastestToSlowest)]
    [HideColumns("StdDev")] // modern BDN: hide by column name
    [SimpleJob(RuntimeMoniker.Net10_0, launchCount: 1, warmupCount: 3, iterationCount: 10)]
    public class STMPerformanceBenchmark
    {
        // ---------------- OperationsPerInvoke tiers ----------------
        // Counts are sized for a fast suite (tens of seconds total) while still amortizing the timer
        // resolution so the per-operation Mean is precise. They are not sized to clear the 100ms
        // MinIterationTime threshold; that warning is suppressed by BenchmarkConfig instead.

        // Direct, non-transactional field read. This is genuinely a few nanoseconds, so even ten million
        // reads sit near the inner-loop overhead itself; treat the read baseline as indicative, not exact.
        private const int OpsRead = 10_000_000;

        // Direct, non-transactional read-modify-write, around one microsecond per operation.
        private const int OpsWrite = 100_000;

        // Single uncontended transaction, around three microseconds per operation. Covers the generic and
        // non-generic atomic paths and the fine-grained dictionary value read and update.
        private const int OpsAtomic = 30_000;

        // Structural dictionary mutation transactions (copy-on-write of the directory snapshot). Counted as
        // individual structural transactions: the loop runs OpsStructural / 2 insert plus remove pairs, so
        // every transaction is one structural mutation on a constant sixteen-key directory. Must stay even.
        private const int OpsStructural = 24_000;

        // Eight-writer contended commit. Around thirty microseconds per round once retries are bounded by
        // the spin phase. Each round spawns and awaits eight transactional writers.
        private const int OpsContended = 3_000;

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
        // structural benchmark always starts from a directory that does not contain it.
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

        [Benchmark(Baseline = true, OperationsPerInvoke = OpsWrite)]
        public int WriteVariable()
        {
            // direct non-transactional read-modify-write (for reference)
            int last = 0;
            for (int i = 0; i < OpsWrite; i++)
            {
                _variable.Write(_variable.Read() + 1);
                last = _variable.Read();
            }
            return last;
        }

        [Benchmark(OperationsPerInvoke = OpsRead)]
        public long ReadVariable()
        {
            // direct non-transactional read; accumulate so the loop is not elided
            long sum = 0;
            for (int i = 0; i < OpsRead; i++)
            {
                sum += _variable.Read();
            }
            return sum;
        }

        // ---------------- Transactional ----------------

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task AtomicWrite()
        {
            for (int i = 0; i < OpsAtomic; i++)
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
        }

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task AtomicReadOnly()
        {
            for (int i = 0; i < OpsAtomic; i++)
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

        // ---- Non-generic API (no per-call LegacyTransactionView adapter allocation) ----

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task AtomicWriteNonGeneric()
        {
            for (int i = 0; i < OpsAtomic; i++)
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
        }

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task AtomicReadOnlyNonGeneric()
        {
            for (int i = 0; i < OpsAtomic; i++)
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
        }

        // ---- Contended write (exercises the retry path and its per-attempt allocations) ----

        [Benchmark(OperationsPerInvoke = OpsContended)]
        public async Task AtomicWriteContended()
        {
            const int writers = 8;
            var shared = _variable; // hoisted: one closure for the whole method, not one per round
            var tasks = new Task[writers];

            for (int round = 0; round < OpsContended; round++)
            {
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
        }

        // ---- Transactional dictionary (fine-grained values, coarse structure) ----

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task DictionarySetExisting()
        {
            // Fine-grained value update on an existing key: writes only that key's value cell,
            // no structural change to the directory.
            for (int i = 0; i < OpsAtomic; i++)
            {
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
        }

        [Benchmark(OperationsPerInvoke = OpsStructural)]
        public async Task DictionaryStructuralMutation()
        {
            // Structural path: each transaction copies the directory snapshot (copy-on-write over the
            // seeded keys) and publishes a new one. To stay repeatable at a constant directory size, the
            // loop alternates a structural insert and a structural remove of AbsentKey. Both are full
            // copy-on-write transactions, so each one is a single structural mutation on a sixteen-key
            // directory: the reported per-operation Mean is the cost of one such transaction, directly
            // comparable to the former single structural insert.
            for (int i = 0; i < OpsStructural / 2; i++)
            {
                await STMEngine.Atomic(tx =>
                {
                    _dict.Set(tx, AbsentKey, 1); // insert: directory grows from sixteen to seventeen
                },
                maxAttempts: MaxAttempts,
                initialBackoffMilliseconds: InitialBackoffMilliseconds,
                backoffType: Backoff,
                readOnly: false,
                cancellationToken: CancellationToken.None);

                await STMEngine.Atomic(tx =>
                {
                    _dict.Remove(tx, AbsentKey); // remove: directory returns to sixteen
                },
                maxAttempts: MaxAttempts,
                initialBackoffMilliseconds: InitialBackoffMilliseconds,
                backoffType: Backoff,
                readOnly: false,
                cancellationToken: CancellationToken.None);
            }
        }

        [Benchmark(OperationsPerInvoke = OpsAtomic)]
        public async Task DictionaryTryGet()
        {
            // Read path: reads the directory snapshot and the key's value cell.
            for (int i = 0; i < OpsAtomic; i++)
            {
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
}