// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Interfaces;
using System.Diagnostics;

namespace STMSharp.Benchmarking
{
    class Program
    {
        // Configuration parameters for the benchmark
        private static int NumberOfThreads = 4;
        private static int NumberOfOperations = 1000;
        private static int BackoffTime = 100;

        static async Task Main(string[] args)
        {
            // STM variable to be shared among threads
            var sharedVar = new STMVariable<int>(0);

            // Stopwatch to measure the total benchmark time
            var stopwatch = Stopwatch.StartNew();

            // Run the benchmark with concurrent threads
            await RunBenchmark(sharedVar, NumberOfThreads, NumberOfOperations);

            stopwatch.Stop();
            Console.WriteLine($"Benchmark completed in {stopwatch.ElapsedMilliseconds}ms.");

            // Calculate time per operation
            double timePerOperation = (double)stopwatch.ElapsedMilliseconds / (NumberOfThreads * NumberOfOperations);
            Console.WriteLine($"Time per operation: {timePerOperation} ms");
            Console.WriteLine($"Total conflicts resolved: {Transaction<int>.ConflictCount}");
            Console.WriteLine($"Total retries attempted: {Transaction<int>.RetryCount}");
        }

        /// <summary>
        /// Runs the benchmark by simulating concurrent STM transactions.
        /// </summary>
        /// <param name="sharedVar">The shared STM variable among threads.</param>
        /// <param name="numberOfThreads">The number of concurrent threads.</param>
        /// <param name="operationsPerThread">The number of operations each thread should perform.</param>
        static async Task RunBenchmark(ISTMVariable<int> sharedVar, int numberOfThreads, int operationsPerThread)
        {
            // Create a list of tasks to run the transactions concurrently
            var tasks = new List<Task>();

            for (int i = 0; i < numberOfThreads; i++)
            {
                tasks.Add(Task.Run(() => ExecuteTransactions(sharedVar, operationsPerThread)));
            }

            // Wait for all threads to complete
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Executes STM transactions for the specified number of operations.
        /// </summary>
        /// <param name="sharedVar">The shared STM variable to be accessed in the transactions.</param>
        /// <param name="operations">The number of operations each thread will perform.</param>
        static void ExecuteTransactions(ISTMVariable<int> sharedVar, int operations)
        {
            // Perform STM transactions for the specified number of operations
            for (int i = 0; i < operations; i++)
            {
                // Execute a transaction using STMEngine with retry mechanism
                STMEngine.Atomic<int>((transaction) =>
                {
                    var value = transaction.Read(sharedVar);
                    transaction.Write(sharedVar, value + 1);
                });
            }
        }
    }
}