// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// Exercises the non-generic transaction surface: a single transaction that reads
    /// and writes variables of different element types, committing them atomically.
    /// </summary>
    [Collection("STM non-parallel")]
    public class HeterogeneousTransactionTests
    {
        [Fact]
        public async Task SingleTransaction_SpansIntAndString()
        {
            var counter = new STMVariable<int>(0);
            var label = new STMVariable<string>("init");

            await STMEngine.Atomic(tx =>
            {
                var current = tx.Read(counter);
                tx.Write(counter, current + 1);
                tx.Write(label, "updated");
            });

            Assert.Equal(1, counter.Read());
            Assert.Equal("updated", label.Read());
        }

        [Fact]
        public async Task ReadYourOwnWrites_ReturnsPendingValueAcrossTypes()
        {
            var number = new STMVariable<int>(1);
            var text = new STMVariable<string>("a");

            int observedNumber = 0;
            string observedText = string.Empty;

            await STMEngine.Atomic(tx =>
            {
                tx.Write(number, 42);
                tx.Write(text, "z");

                // Both reads must observe the transaction's own pending writes.
                observedNumber = tx.Read(number);
                observedText = tx.Read(text);
            });

            Assert.Equal(42, observedNumber);
            Assert.Equal("z", observedText);
            Assert.Equal(42, number.Read());
            Assert.Equal("z", text.Read());
        }

        [Fact]
        public async Task HeterogeneousCommit_IsAtomicAcrossTypes_UnderContention()
        {
            var count = new STMVariable<int>(0);
            var lastWriter = new STMVariable<string>("none");
            const int workers = 16;

            var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
            {
                await STMEngine.Atomic(tx =>
                {
                    var current = tx.Read(count);
                    tx.Write(count, current + 1);
                    tx.Write(lastWriter, $"worker-{i}");
                },
                maxAttempts: 128,
                initialBackoffMilliseconds: 1,
                backoffType: BackoffType.ExponentialWithJitter);
            })).ToArray();

            await Task.WhenAll(tasks);

            // The int counter must reflect every committed transaction with no lost updates,
            // and the string must hold a value written by one of the committing workers.
            Assert.Equal(workers, count.Read());
            Assert.StartsWith("worker-", lastWriter.Read());
        }

        [Fact]
        public async Task ReadOnlyTransaction_RejectsWrite()
        {
            var v = new STMVariable<int>(0);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await STMEngine.Atomic(tx =>
                {
                    _ = tx.Read(v);
                    tx.Write(v, 1); // not allowed in a read-only transaction
                },
                readOnly: true));
        }
    }
}
