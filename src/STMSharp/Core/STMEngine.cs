// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    /// <summary>
    /// STMEngine provides methods to execute atomic operations within 
    /// Software Transactional Memory (STM) using a retry and backoff strategy.
    /// 
    /// This class allows for configurable retry attempts and backoff timings, 
    /// enabling robust handling of transaction conflicts in concurrent environments.
    /// 
    /// Key Features:
    /// - Implements an exponential backoff mechanism to minimize contention.
    /// - Provides default configurations, with the flexibility to override as needed.
    /// 
    /// Usage Example:
    /// <code>
    /// await STMEngine.Atomic<int>(transaction =>
    /// {
    ///     var value = transaction.Read(sharedVar);
    ///     transaction.Write(sharedVar, value + 1);
    /// }, maxAttempts: 5, initialBackoffMilliseconds: 200);
    /// </code>
    /// </summary>
    public class STMEngine
    {
        private const int DefaultMaxAttempts = 3;
        private const int DefaultInitialBackoffMilliseconds = 100;

        /// <summary>
        /// Executes a synchronous action within an STM transaction with retry.
        /// </summary>
        public static async Task Atomic<T>(
            Action<Transaction<T>> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        {
            // Wrap the synchronous action into the asynchronous overload
            await Atomic<T>(tx =>
            {
                action(tx);
                return Task.CompletedTask;
            }, maxAttempts, initialBackoffMilliseconds);
        }

        /// <summary>
        /// Executes an asynchronous function within an STM transaction with retry and backoff.
        /// </summary>
        public static async Task Atomic<T>(
            Func<Transaction<T>, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        {
            int attempt = 0;
            int backoffTime = initialBackoffMilliseconds;

            // Retry loop for transaction attempts
            while (attempt < maxAttempts)
            {
                // Create a new transaction instance for each attempt
                var transaction = new Transaction<T>();

                // Execute user-provided transactional logic
                await func(transaction);

                // Attempt to commit: returns true if no conflict detected
                if (transaction.Commit())
                {
                    // Commit successful, exit method
                    return;
                }

                // Conflict detected: wait before retrying
                await Task.Delay(backoffTime);
                // Exponential backoff for next attempt
                backoffTime *= 2;
                attempt++;
            }

            // All attempts failed: throw timeout exception
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }
    }
}
