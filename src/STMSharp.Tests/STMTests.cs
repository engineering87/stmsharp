// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;

namespace STMSharp.Tests
{
    public class STMTests
    {
        private async Task<int> ReadInsideTransactionAsync(STMVariable<int> sharedVar)
        {
            int result = 0;
            await STMEngine.Atomic<int>((transaction) =>
            {
                result = transaction.Read(sharedVar);
            });
            return result;
        }

        [Fact]
        public async Task TestTransactionWithBackoffAsync()
        {
            var sharedVar = new STMVariable<int>(0);

            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            Assert.Equal(2, await ReadInsideTransactionAsync(sharedVar));
        }

        [Fact]
        public async Task TestTransactionWithConflictAsync()
        {
            var sharedVar = new STMVariable<int>(0);

            var task1 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            var task2 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            await Task.WhenAll(task1, task2);

            Assert.Equal(2, await ReadInsideTransactionAsync(sharedVar));
        }

        [Fact]
        public async Task TestTransactionWithMultipleRetriesAsync()
        {
            var sharedVar = new STMVariable<int>(0);

            var task1 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            var task2 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            await Task.WhenAll(task1, task2);

            Assert.Equal(2, await ReadInsideTransactionAsync(sharedVar));
        }

        [Fact]
        public async Task TestTransactionWithNoConflictAsync()
        {
            var sharedVar = new STMVariable<int>(0);

            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            Assert.Equal(2, await ReadInsideTransactionAsync(sharedVar));
        }

        [Fact]
        public async Task TestTransactionWithMaxAttemptsAsync()
        {
            var sharedVar = new STMVariable<int>(0);

            var task1 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            var task2 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            await Task.WhenAll(task1, task2);

            Assert.Equal(2, await ReadInsideTransactionAsync(sharedVar));
        }

        [Fact]
        public async Task TestHighContentionWithMultipleTransactionsAsync()
        {
            var sharedVar = new STMVariable<int>(0);
            const int transactionCount = 10;

            var tasks = Enumerable.Range(0, transactionCount).Select(_ =>
                STMEngine.Atomic<int>((transaction) =>
                {
                    var value = transaction.Read(sharedVar);
                    transaction.Write(sharedVar, value + 1);
                })
            ).ToList();

            await Task.WhenAll(tasks);

            Assert.Equal(transactionCount, await ReadInsideTransactionAsync(sharedVar));
        }
    }
}