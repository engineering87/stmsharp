// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Comparative
{
    /// <summary>
    /// The complement to the single-counter worst case: a disjoint-access workload where
    /// each thread operates only on its own cell, so no two threads ever touch the same
    /// variable. Logically there is no contention at all; the only question is whether the
    /// synchronization mechanism recognizes that and lets the independent work proceed in
    /// parallel.
    ///
    /// Two implementations run the identical workload:
    ///   - GlobalLockBaseline: a single lock guards the whole array, so it serializes
    ///     work that is in fact independent. This is the case a global lock handles
    ///     badly, and it is exactly the situation an STM is designed for.
    ///   - STMSharp: each thread commits transactions over its own cell; since the read
    ///     and write sets of different threads are disjoint, transactions do not conflict
    ///     and can commit concurrently.
    ///
    /// Honesty note: the global lock is deliberately the weak baseline here, just as the
    /// single shared counter was the weak case for STM. A per-cell fine-grained lock would
    /// be competitive with STMSharp on this exact workload, because the access pattern is
    /// statically disjoint. The pair of benchmarks (single-counter contention plus disjoint
    /// access) is meant to show both ends of the spectrum, not to stack the deck. The
    /// place STMSharp earns its keep over fine-grained locking is dynamic, data-dependent
    /// access patterns and multi-variable atomicity, which a future benchmark should cover.
    ///
    /// Each benchmark performs exactly Threads * OpsPerThread increments and verifies every
    /// cell equals OpsPerThread, so a run that loses updates fails loudly.
    ///
    /// NOTE: not compiled in the authoring environment. Validate locally before trusting
    /// any numbers.
    /// </summary>
    [MemoryDiagnoser]
    public class DisjointAccessBenchmark
    {
        [Params(4, 16)]
        public int Threads;

        [Params(1000)]
        public int OpsPerThread;

        // ---- Global-lock baseline (serializes independent work) ----

        [Benchmark(Baseline = true)]
        public int GlobalLockBaseline()
        {
            var gate = new object();
            var cells = new int[Threads];

            RunPerThread(t =>
            {
                for (int i = 0; i < OpsPerThread; i++)
                {
                    lock (gate)
                    {
                        cells[t] = cells[t] + 1;
                    }
                }
            });

            Verify(cells);
            return cells[0];
        }

        // ---- STMSharp (disjoint transactions commit concurrently) ----

        [Benchmark]
        public int STMSharp()
        {
            var cells = new STMVariable<int>[Threads];
            for (int t = 0; t < Threads; t++)
                cells[t] = new STMVariable<int>(0);

            RunPerThread(t =>
            {
                var cell = cells[t];
                for (int i = 0; i < OpsPerThread; i++)
                {
                    // Disjoint access means conflicts should be rare to absent, but retry
                    // the whole block anyway so budget exhaustion never drops an increment.
                    while (true)
                    {
                        try
                        {
                            STMEngine.Atomic(tx =>
                            {
                                var v = tx.Read(cell);
                                tx.Write(cell, v + 1);
                            },
                            maxAttempts: 64,
                            initialBackoffMilliseconds: 0,
                            maxBackoffMilliseconds: 2,
                            backoffType: BackoffType.ExponentialWithJitter)
                            .GetAwaiter().GetResult();
                            break;
                        }
                        catch (STMSharp.Core.Exceptions.TransactionConflictException)
                        {
                        }
                    }
                }
            });

            var finals = new int[Threads];
            for (int t = 0; t < Threads; t++)
                finals[t] = cells[t].Read();
            Verify(finals);
            return finals[0];
        }

        private void Verify(int[] cells)
        {
            for (int t = 0; t < cells.Length; t++)
            {
                if (cells[t] != OpsPerThread)
                    throw new InvalidOperationException(
                        $"Lost updates in cell {t}: {cells[t]} != {OpsPerThread}");
            }
        }

        // Runs one thread per cell, passing each its own index.
        private void RunPerThread(Action<int> body)
        {
            var threads = new Thread[Threads];
            for (int t = 0; t < Threads; t++)
            {
                int captured = t;
                threads[t] = new Thread(() => body(captured));
                threads[t].Start();
            }
            foreach (var th in threads)
                th.Join();
        }
    }
}
