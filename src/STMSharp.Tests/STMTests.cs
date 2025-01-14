// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;

namespace STMSharp.Tests
{
    public class STMTests
    {
        [Fact]
        public async Task TestTransactionWithBackoffAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Run a transaction to increment the value
            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Run another transaction to increment the value
            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Verify the final result
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public async Task TestTransactionWithConflictAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Run a transaction to increment the value
            var task1 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Run another transaction to increment the value with conflict
            var task2 = STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Await both tasks
            await Task.WhenAll(task1, task2);

            // The final result should still be 2, even with conflict resolution
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public async Task TestTransactionWithMultipleRetriesAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Run multiple transactions that will cause retries due to conflict
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

            // Run the tasks simultaneously
            await Task.WhenAll(task1, task2);

            // After retries and conflict resolution, the final value should be 2
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public async Task TestTransactionWithNoConflictAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Run a transaction to increment the value
            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Run another transaction to increment the value without conflict
            await STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Verify the final result without conflict
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public async Task TestTransactionWithMaxAttemptsAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Simulate multiple transactions causing conflicts
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

            // Await both tasks
            await Task.WhenAll(task1, task2);

            // Verify the final result with max attempts
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public async Task TestHighContentionWithMultipleTransactionsAsync()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Number of concurrent transactions
            const int transactionCount = 10;

            // Create a list of tasks simulating high contention
            var tasks = Enumerable.Range(0, transactionCount).Select(_ =>
                STMEngine.Atomic<int>((transaction) =>
                {
                    var value = transaction.Read(sharedVar);
                    transaction.Write(sharedVar, value + 1);
                })
            ).ToList();

            // Run all transactions concurrently
            await Task.WhenAll(tasks);

            // Verify that the final value matches the number of transactions
            Assert.Equal(transactionCount, sharedVar.Read());
        }
    }
}