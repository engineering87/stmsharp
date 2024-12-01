// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Exceptions;

namespace STMSharp.Tests
{
    public class STMTests
    {
        [Fact]
        public void TestTransactionWithBackoff()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Run a transaction to increment the value
            STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Run another transaction to increment the value
            STMEngine.Atomic<int>((transaction) =>
            {
                var value = transaction.Read(sharedVar);
                transaction.Write(sharedVar, value + 1);
            });

            // Verify the final result
            Assert.Equal(2, sharedVar.Read());
        }

        [Fact]
        public void TestTransactionFailureAfterMaxAttempts()
        {
            // Initialize shared variable
            var sharedVar = new STMVariable<int>(0);

            // Simulate a failure scenario where max retries are exceeded
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                STMEngine.Atomic<int>((transaction) =>
                {
                    // Modify the shared variable in an unpredictable way to force retries
                    var value = transaction.Read(sharedVar);
                    transaction.Write(sharedVar, value + 1);

                    // Simulate a conflict by throwing an exception on each attempt
                    throw new TransactionConflictException("Simulated conflict.");
                });
            });

            Assert.Equal("Transaction failed after maximum retry attempts.", exception.Message);
        }
    }
}