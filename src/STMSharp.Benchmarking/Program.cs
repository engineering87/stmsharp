// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Benchmarking.Config;
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
            // STM shared for the STM benchmark
            var sharedSTMVar = new STMVariable<int>(0);

            PrintHeader("STMSharp Benchmarking");

            // Print benchmarking configuration
            PrintBenchmarkConfig();

            // Stopwatch to measure the total benchmark time
            var stopwatch = Stopwatch.StartNew();

            // Run the benchmark with concurrent threads
            await RunBenchmark(sharedSTMVar);

            stopwatch.Stop();

            // Print result summary
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n{"STM Benchmark completed.".PadLeft(40)}");
            Console.WriteLine($"{"Duration:".PadLeft(30)} {stopwatch.ElapsedMilliseconds} ms");

            double timePerOperation = (double)stopwatch.ElapsedMilliseconds / (Config.NumberOfThreads * Config.NumberOfOperations);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{"Time per operation:".PadLeft(30)} {timePerOperation:F4} ms");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"{"Total conflicts resolved:".PadLeft(30)} {Transaction<int>.ConflictCount}");
            Console.WriteLine($"{"Total retries attempted:".PadLeft(30)} {Transaction<int>.RetryCount}");

            int finalValue = 0;
            // Read final STM value using configured retry settings
            await STMEngine.Atomic<int>(
                tx => { finalValue = tx.Read(sharedSTMVar); },
                Config.MaxAttempts,
                Config.BackoffTime
            );

            Console.WriteLine($"{"Final STM value:".PadLeft(30)} {finalValue}");
            Console.WriteLine($"{"Expected:".PadLeft(30)} {Config.NumberOfThreads * Config.NumberOfOperations}");
            Console.ResetColor();

            // Add a footer to signify the end of the process
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
                // Perform STM transactions for the specified number of operations
                await STMEngine.Atomic<int>(tx =>
                {
                    var value = tx.Read(sharedSTMVar);
                    tx.Write(sharedSTMVar, value + 1);
                });

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
    }
}
