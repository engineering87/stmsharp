// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Comparative
{
    /// <summary>
    /// A single, honest, like-for-like comparison: many threads each performing a
    /// read-modify-write increment of one shared counter, the canonical high-contention
    /// STM workload. Two implementations run the identical workload:
    ///   - a lock-based baseline (lock + plain field), the reference every .NET
    ///     developer already knows,
    ///   - STMSharp with read-modify-write transactions (the general path), and
    ///   - STMSharp with Commute (the commutative path, where increments do not
    ///     conflict logically and only the commit lock is contended).
    ///
    /// The point is not to declare a universal winner from one workload, but to make the
    /// comparison reproducible and to state the method. Each benchmark commits exactly
    /// Threads * IncrementsPerThread increments and verifies the final total, so a run
    /// that loses updates fails loudly rather than reporting a fast but wrong number.
    ///
    /// No third-party STM library is referenced. The comparison is deliberately limited
    /// to STMSharp against a lock-based baseline.
    ///
    /// NOTE: not compiled in the authoring environment. Validate locally before trusting
    /// any numbers.
    /// </summary>
    [MemoryDiagnoser]
    public class ContendedCounterBenchmark
    {
        [Params(4, 16)]
        public int Threads;

        [Params(1000)]
        public int IncrementsPerThread;

        private int Expected => Threads * IncrementsPerThread;

        // ---- Lock-based baseline ----

        [Benchmark(Baseline = true)]
        public int LockBaseline()
        {
            var gate = new object();
            int counter = 0;

            RunThreads(() =>
            {
                for (int i = 0; i < IncrementsPerThread; i++)
                {
                    lock (gate)
                    {
                        counter++;
                    }
                }
            });

            if (counter != Expected)
                throw new InvalidOperationException($"Lost updates: {counter} != {Expected}");
            return counter;
        }

        // ---- STMSharp ----

        [Benchmark]
        public int STMSharp()
        {
            var shared = new STMVariable<int>(0);

            RunThreads(() =>
            {
                for (int i = 0; i < IncrementsPerThread; i++)
                {
                    // Retry the whole block so budget exhaustion never drops an increment.
                    while (true)
                    {
                        try
                        {
                            STMEngine.Atomic(tx =>
                            {
                                var v = tx.Read(shared);
                                tx.Write(shared, v + 1);
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

            int final = shared.Read();
            if (final != Expected)
                throw new InvalidOperationException($"Lost updates: {final} != {Expected}");
            return final;
        }

        // ---- STMSharp using Commute ----

        [Benchmark]
        public int STMSharp_Commute()
        {
            var shared = new STMVariable<int>(0);

            RunThreads(() =>
            {
                for (int i = 0; i < IncrementsPerThread; i++)
                {
                    // A commuting increment never conflicts logically with another increment,
                    // so the retry loop is unnecessary; the commit waits for the lock in order
                    // and then applies the increment to the live committed value.
                    STMEngine.Atomic(
                        tx => tx.Commute(shared, x => x + 1),
                        maxAttempts: 64,
                        initialBackoffMilliseconds: 0,
                        maxBackoffMilliseconds: 2,
                        backoffType: BackoffType.ExponentialWithJitter)
                        .GetAwaiter().GetResult();
                }
            });

            int final = shared.Read();
            if (final != Expected)
                throw new InvalidOperationException($"Lost updates: {final} != {Expected}");
            return final;
        }

        private void RunThreads(Action body)
        {
            var threads = new Thread[Threads];
            for (int t = 0; t < Threads; t++)
            {
                threads[t] = new Thread(() => body());
                threads[t].Start();
            }
            foreach (var th in threads)
                th.Join();
        }
    }
}
