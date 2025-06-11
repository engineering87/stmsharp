// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Benchmarking.Config;
using STMSharp.Benchmarking.Models;
using STMSharp.Core;
using STMSharp.Core.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace STMSharp.Benchmarking
{
    class Program
    {
        // Configuration parameters for the benchmark
        private static readonly BenchmarkConfig Config;
        private static int sharedLockValue = 0;
        private static readonly object lockObj = new();

        static Program()
        {
            Config = LoadConfiguration("appsettings.json");
        }

        /// <summary>
        /// Loads the configuration from a JSON file.
        /// </summary>
        private static BenchmarkConfig LoadConfiguration(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<BenchmarkConfig>(json)
                    ?? throw new InvalidOperationException("Invalid configuration format.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                Console.ResetColor();
                throw;
            }
        }

        static async Task Main()
        {
            PrintHeader("STMSharp Benchmarking");

            PrintBenchmarkConfig();

            // STM benchmark
            var stmVar = new STMVariable<int>(0);
            var stmWatch = Stopwatch.StartNew();
            await RunBenchmark(stmVar);
            stmWatch.Stop();

            int finalSTMValue = 0;
            await STMEngine.Atomic<int>(
                tx => finalSTMValue = tx.Read(stmVar),
                Config.MaxAttempts,
                Config.BackoffTime
            );

            var stmResult = new BenchmarkResult
            {
                Mode = "STM",
                DurationMs = stmWatch.ElapsedMilliseconds,
                TimePerOperation = (double)stmWatch.ElapsedMilliseconds / (Config.NumberOfThreads * Config.NumberOfOperations),
                FinalValue = finalSTMValue,
                ConflictCount = Transaction<int>.ConflictCount,
                RetryCount = Transaction<int>.RetryCount
            };

            // LOCK benchmark
            sharedLockValue = 0;
            var lockWatch = Stopwatch.StartNew();
            await RunLockBenchmark();
            lockWatch.Stop();

            var lockResult = new BenchmarkResult
            {
                Mode = "LOCK",
                DurationMs = lockWatch.ElapsedMilliseconds,
                TimePerOperation = (double)lockWatch.ElapsedMilliseconds / (Config.NumberOfThreads * Config.NumberOfOperations),
                FinalValue = sharedLockValue
            };

            PrintResults(stmResult, lockResult);

            PrintFooter("Benchmark Results Summary");
        }

        /// <summary>
        /// Runs the benchmark by simulating concurrent STM transactions.
        /// </summary>
        /// <param name="sharedSTMVar">The shared STM variable among threads.</param>
        static async Task RunBenchmark(ISTMVariable<int> sharedSTMVar)
        {
            // Create a list of tasks to run the transactions concurrently
            var tasks = new List<Task>();

            for (int i = 0; i < Config.NumberOfThreads; i++)
            {
                tasks.Add(Task.Run(async () => await ExecuteTransactions(sharedSTMVar)));
            }

            // Wait for all threads to complete
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Executes STM transactions for the specified number of operations.
        /// </summary>
        /// <param name="sharedSTMVar">The shared STM variable to be accessed in the transactions.</param>
        static async Task ExecuteTransactions(ISTMVariable<int> sharedSTMVar)
        {
            try
            {
                for (int i = 0; i < Config.NumberOfOperations; i++)
                {
                    // Perform STM transactions for the specified number of operations
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var value = tx.Read(sharedSTMVar);
                        tx.Write(sharedSTMVar, value + 1);
                    });
                }

                await Task.Delay(Config.ProcessingTime);
            }
            catch (TimeoutException ex)
            {
                // Log timeout and continue benchmark
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: transaction timed out after max attempts: {ex.Message}");
                Console.ResetColor();
            }
        }

        static async Task RunLockBenchmark()
        {
            var tasks = new List<Task>();
            for (int i = 0; i < Config.NumberOfThreads; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int j = 0; j < Config.NumberOfOperations; j++)
                    {
                        lock (lockObj)
                        {
                            sharedLockValue++;
                        }
                    }
                    Thread.Sleep(Config.ProcessingTime);
                }));
            }
            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Prints a formatted header for the program.
        /// </summary>
        static void PrintHeader(string title)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;

            // Calculate the padding needed to center the title
            int totalPadding = 60 - title.Length;
            int leftPadding = totalPadding / 2;
            int rightPadding = totalPadding - leftPadding;

            // Print the header with the title centered
            Console.WriteLine(new string('=', 60));
            Console.WriteLine(new string(' ', leftPadding) + title.ToUpper() + new string(' ', rightPadding));
            Console.WriteLine(new string('=', 60));

            Console.ResetColor();
        }

        /// <summary>
        /// Prints a formatted footer for the program.
        /// </summary>
        static void PrintFooter(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;

            // Calculate the padding needed to center the message
            int totalPadding = 60 - message.Length;
            int leftPadding = totalPadding / 2;
            int rightPadding = totalPadding - leftPadding;

            // Print the footer with the message centered
            Console.WriteLine("\n" + new string('=', 60));
            Console.WriteLine(new string(' ', leftPadding) + message.ToUpper() + new string(' ', rightPadding));
            Console.WriteLine(new string('=', 60) + "\n");

            Console.ResetColor();
        }

        /// <summary>
        /// Prints the benchmarking configuration parameters.
        /// </summary>
        static void PrintBenchmarkConfig()
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"{"Threads:".PadLeft(30)} {Config.NumberOfThreads}");
            Console.WriteLine($"{"Operations per Thread:".PadLeft(30)} {Config.NumberOfOperations}");
            Console.WriteLine($"{"Backoff Time (ms):".PadLeft(30)} {Config.BackoffTime}");
            Console.WriteLine(new string('-', 60));
            Console.ResetColor();
        }

        static void PrintResults(BenchmarkResult stm, BenchmarkResult lck)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n{"RESULT COMPARISON".PadLeft(40)}");
            Console.ResetColor();

            Console.WriteLine($"\n{"Metric".PadRight(25)} | {"STM".PadRight(15)} | {"LOCK".PadRight(15)}");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine($"{"Duration (ms)".PadRight(25)} | {stm.DurationMs.ToString().PadRight(15)} | {lck.DurationMs.ToString().PadRight(15)}");
            Console.WriteLine($"{"Time per operation".PadRight(25)} | {stm.TimePerOperation:F4} ms".PadRight(15) + $" | {lck.TimePerOperation:F4} ms".PadRight(15));
            Console.WriteLine($"{"Final Value".PadRight(25)} | {stm.FinalValue.ToString().PadRight(15)} | {lck.FinalValue.ToString().PadRight(15)}");
            Console.WriteLine($"{"Conflicts Resolved".PadRight(25)} | {stm.ConflictCount.ToString().PadRight(15)} | {"N/A".PadRight(15)}");
            Console.WriteLine($"{"Retries Attempted".PadRight(25)} | {stm.RetryCount.ToString().PadRight(15)} | {"N/A".PadRight(15)}");
            Console.WriteLine(new string('-', 60));
        }
    }
}
