// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// Verifies opacity: a running transaction always observes a consistent snapshot,
    /// never a torn intermediate state produced by a concurrent commit. A reader that
    /// would otherwise see an inconsistent view aborts and retries instead.
    /// </summary>
    [Collection("STM non-parallel")]
    public class OpacityTests
    {
        [Fact]
        public async Task Reader_NeverObservesTornCrossVariableState()
        {
            // Invariant maintained by writers: a + b == Total at every committed state.
            const int total = 1_000;
            var a = new STMVariable<int>(total);
            var b = new STMVariable<int>(0);

            int violations = 0;
            using var stop = new CancellationTokenSource();

            // Single writer moving value between the two variables, always atomically.
            var writer = Task.Run(async () =>
            {
                var rng = new Random(20250530);
                while (!stop.IsCancellationRequested)
                {
                    await STMEngine.Atomic(tx =>
                    {
                        var av = tx.Read(a);
                        var bv = tx.Read(b);
                        int delta = rng.Next(1, 64);
                        if (av - delta >= 0)
                        {
                            tx.Write(a, av - delta);
                            tx.Write(b, bv + delta);
                        }
                    },
                    maxAttempts: 256,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter);
                }
            });

            // Readers that read both variables and check the invariant inside the transaction.
            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    await STMEngine.Atomic(tx =>
                    {
                        var av = tx.Read(a);
                        var bv = tx.Read(b);
                        if (av + bv != total)
                            Interlocked.Increment(ref violations);
                    },
                    maxAttempts: 256,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    readOnly: true);
                }
            })).ToArray();

            await Task.WhenAll(readers);
            stop.Cancel();
            await writer;

            // Under opacity, no reader transaction can ever observe a + b != total.
            Assert.Equal(0, violations);

            // The invariant must also hold in the final committed state.
            Assert.Equal(total, a.Read() + b.Read());
        }

        [Fact]
        public async Task RepeatedRead_WithinTransaction_IsStable()
        {
            // A transaction reading the same variable twice must observe the same value,
            // even across concurrent direct writes: an inconsistent second read aborts
            // and the transaction retries until it sees a consistent snapshot.
            var v = new STMVariable<int>(0);
            using var stop = new CancellationTokenSource();

            var writer = Task.Run(() =>
            {
                int n = 1;
                while (!stop.IsCancellationRequested)
                {
                    v.Write(n++);
                    // Small pause so the reader is not starved out of clean read windows.
                    Thread.SpinWait(100);
                }
            });

            int mismatches = 0;
            for (int i = 0; i < 5_000; i++)
            {
                await STMEngine.Atomic(tx =>
                {
                    var first = tx.Read(v);
                    var second = tx.Read(v);
                    if (first != second)
                        Interlocked.Increment(ref mismatches);
                },
                maxAttempts: 512,
                initialBackoffMilliseconds: 1,
                backoffType: BackoffType.ExponentialWithJitter,
                readOnly: true);
            }

            stop.Cancel();
            await writer;

            Assert.Equal(0, mismatches);
        }
    }
}
