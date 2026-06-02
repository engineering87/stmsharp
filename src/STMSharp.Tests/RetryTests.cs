// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Diagnostics;
using STMSharp.Core;

namespace STMSharp.Tests
{
    /// <summary>
    /// Verifies blocking composition via ITransaction.Retry: a transaction that cannot make
    /// progress blocks on its read set and is woken when another transaction commits a change
    /// to one of those variables, then re-executes from the start.
    ///
    /// The producer/consumer test is the important one: if the wake-up is ever lost, the
    /// consumer never completes and the test fails by timeout rather than passing silently.
    /// </summary>
    [Collection("STM non-parallel")]
    public class RetryTests
    {
        [Fact]
        public async Task Retry_WithEmptyReadSet_Throws()
        {
            var v = new STMVariable<int>(0);

            // Retry before any Read has nothing to wake it; the engine must surface the
            // InvalidOperationException rather than block forever.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await STMEngine.Atomic(tx =>
                {
                    tx.Retry();
                });
            });
        }

        [Fact]
        public async Task Retry_BlocksUntilWatchedVariableChanges()
        {
            var flag = new STMVariable<int>(0);

            // A consumer that only proceeds once flag becomes non-zero. While it is zero,
            // the consumer reads it (entering the read set) and then Retry()s, blocking.
            var consumer = Task.Run(async () =>
            {
                int observed = 0;
                await STMEngine.Atomic(tx =>
                {
                    var current = tx.Read(flag);
                    if (current == 0)
                        tx.Retry();   // block on flag until a producer commits a change
                    observed = current;
                });
                return observed;
            });

            // The consumer should not have completed yet: it is parked on flag.
            Assert.False(consumer.IsCompleted, "Consumer should be blocked while flag is zero.");

            // Give it a moment to actually park, then publish a change to wake it.
            await Task.Delay(100);
            Assert.False(consumer.IsCompleted, "Consumer should still be blocked before the producer commits.");

            await STMEngine.Atomic(tx => tx.Write(flag, 42));

            // The wake-up must let the consumer re-execute and complete promptly.
            var completed = await Task.WhenAny(consumer, Task.Delay(5000));
            Assert.Same(consumer, completed);
            Assert.Equal(42, await consumer);
        }

        [Fact]
        public async Task ProducerConsumer_OneItemBuffer_TransfersAllItems()
        {
            // A one-slot buffer: 0 means empty, non-zero means full with that value.
            // The consumer Retry()s while empty; the producer Retry()s while full.
            // If any wake-up is lost, the run hangs and the outer timeout fails the test,
            // so a green run is real evidence the blocking path works end to end.
            var slot = new STMVariable<int>(0);
            const int itemCount = 200;

            var produced = new List<int>();
            var consumed = new List<int>();

            var producer = Task.Run(async () =>
            {
                for (int i = 1; i <= itemCount; i++)
                {
                    await STMEngine.Atomic(tx =>
                    {
                        if (tx.Read(slot) != 0)
                            tx.Retry();          // buffer full: wait for the consumer
                        tx.Write(slot, i);
                    });
                    produced.Add(i);
                }
            });

            var consumer = Task.Run(async () =>
            {
                for (int i = 0; i < itemCount; i++)
                {
                    int item = 0;
                    await STMEngine.Atomic(tx =>
                    {
                        var v = tx.Read(slot);
                        if (v == 0)
                            tx.Retry();          // buffer empty: wait for the producer
                        item = v;
                        tx.Write(slot, 0);       // mark consumed
                    });
                    consumed.Add(item);
                }
            });

            var all = Task.WhenAll(producer, consumer);
            var finished = await Task.WhenAny(all, Task.Delay(30000));
            Assert.Same(all, finished); // fail by timeout if a wake-up was lost

            Assert.Equal(itemCount, consumed.Count);
            Assert.Equal(Enumerable.Range(1, itemCount), consumed);
        }

        [Fact]
        public async Task Retry_SafetyValveTimeout_DoesNotHangForever()
        {
            // Nothing will ever change this variable, so the only way the transaction can
            // complete is the safety-valve timeout re-executing it. The first read returns a
            // value that lets it complete on the second attempt path; here we assert that the
            // call returns within a bounded time rather than hanging.
            var v = new STMVariable<int>(0);
            int attempts = 0;

            var sw = Stopwatch.StartNew();
            await STMEngine.Atomic(tx =>
            {
                int n = tx.Read(v);
                attempts++;
                // Block only on the first attempt; on the timeout-driven re-execution, proceed.
                if (attempts == 1)
                    tx.Retry();
            });
            sw.Stop();

            Assert.True(attempts >= 2, "The transaction should have re-executed after the safety-valve timeout.");
            // The safety valve is ~1s; allow generous headroom for slow CI.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Took too long: {sw.Elapsed}.");
        }
    }
}
