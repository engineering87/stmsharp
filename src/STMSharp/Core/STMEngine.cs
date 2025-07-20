// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Backoff;
using STMSharp.Enum;

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
    /// - Supports multiple backoff algorithms including exponential and jitter-based.
    /// - Handles automatic retries for transactional conflicts.
    /// - Allows both synchronous and asynchronous transactional logic.
    /// - Supports read-only transactions to avoid unnecessary writes and improve safety.
    ///
    /// Usage Example:
    /// <code>
    /// await STMEngine.Atomic<int>(transaction =>
    /// {
    ///     var value = transaction.Read(sharedVar);
    ///     transaction.Write(sharedVar, value + 1);
    /// }, maxAttempts: 5, initialBackoffMilliseconds: 200, backoffType: BackoffType.ExponentialWithJitter);
    /// </code>
    ///
    /// Read-only usage:
    /// <code>
    /// await STMEngine.Atomic<int>(transaction =>
    /// {
    ///     var value = transaction.Read(sharedVar);
    ///     Console.WriteLine(value);
    /// }, readOnly: true);
    /// </code>
    /// </summary>
    public class STMEngine
    {
        private const int DefaultMaxAttempts = 3;
        private const int DefaultInitialBackoffMilliseconds = 100;

        private const BackoffType DefaultBackoffType = BackoffType.ExponentialWithJitter;

        /// <summary>
        /// Executes a asynchronous transactional action with automatic retries in case of conflict.
        /// </summary>
        /// <typeparam name="T">The return type of the STM transaction.</typeparam>
        /// <param name="action">A user-defined synchronous action containing transactional logic.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts before failing.</param>
        /// <param name="initialBackoffMilliseconds">The base delay used for calculating backoff between retries.</param>
        /// <param name="backoffType">The backoff algorithm to apply on conflict (e.g., exponential, jitter, constant).</param>
        /// <param name="readOnly">Whether the transaction should be executed in read-only mode (disallows writes).</param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>A task that completes once the transaction is successfully committed or throws on failure.</returns>

        public static async Task Atomic<T>(
            Action<Transaction<T>> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            // Wrap the synchronous action into the asynchronous overload
            await Atomic<T>(tx =>
            {
                action(tx);
                return Task.CompletedTask;
            }, maxAttempts, initialBackoffMilliseconds, backoffType, readOnly, cancellationToken);
        }

        /// <summary>
        /// Executes an asynchronous transactional function with automatic retries in case of conflict.
        /// </summary>
        /// <typeparam name="T">The return type of the STM transaction.</typeparam>
        /// <param name="func">A user-defined asynchronous function containing transactional logic.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts before failing.</param>
        /// <param name="initialBackoffMilliseconds">The base delay used for calculating backoff between retries.</param>
        /// <param name="backoffType">The backoff algorithm to apply on conflict (e.g., exponential, jitter, constant).</param>
        /// <param name="readOnly">Whether the transaction should be executed in read-only mode (disallows writes).</param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>A task that completes once the transaction is successfully committed or throws on failure.</returns>
        public static async Task Atomic<T>(
            Func<Transaction<T>, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            BackoffType backoffType = BackoffType.ExponentialWithJitter,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            int attempt = 0;
            int backoffTime = initialBackoffMilliseconds;

            // Retry loop for transaction attempts
            while (attempt < maxAttempts)
            {
                // Check for cancellation request
                cancellationToken.ThrowIfCancellationRequested();

                // Create a new transaction instance for each attempt
                var transaction = new Transaction<T>(readOnly);

                // Execute user-provided transactional logic
                await func(transaction);

                // Attempt to commit: returns true if no conflict detected
                if (transaction.Commit())
                {
                    // Commit successful, exit method
                    return;
                }

                // Conflict detected: wait before retrying
                int delay = BackoffPolicy.GetDelayMilliseconds(backoffType, attempt, initialBackoffMilliseconds);
                await Task.Delay(delay, cancellationToken);

                attempt++;
            }

            // All attempts failed: throw timeout exception
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }
    }
}
