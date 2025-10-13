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
        /// Executes an asynchronous transactional action with automatic retries in case of conflict.
        /// </summary>
        /// <typeparam name="T">
        /// The type of STM variables involved in the transaction (i.e., Transaction&lt;T&gt;).
        /// This is not a return type; the method completes when the transaction commits or throws on failure.
        /// </typeparam>
        /// <param name="action">A user-defined synchronous action containing transactional logic.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts before failing.</param>
        /// <param name="initialBackoffMilliseconds">The base delay used for calculating backoff between retries.</param>
        /// <param name="backoffType">The backoff algorithm to apply on conflict (e.g., exponential, jitter, constant).</param>
        /// <param name="readOnly">Whether the transaction should be executed in read-only mode (disallows writes).</param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>
        /// A task that completes when the transaction is successfully committed; otherwise throws if all attempts fail
        /// or if the operation is cancelled.
        /// </returns>
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
        /// <typeparam name="T">
        /// The type of STM variables involved in the transaction (i.e., Transaction&lt;T&gt;).
        /// This is not a return type; the method completes when the transaction commits or throws on failure.
        /// </typeparam>
        /// <param name="func">A user-defined asynchronous function containing transactional logic.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts before failing.</param>
        /// <param name="initialBackoffMilliseconds">The base delay used for calculating backoff between retries.</param>
        /// <param name="backoffType">The backoff algorithm to apply on conflict (e.g., exponential, jitter, constant).</param>
        /// <param name="readOnly">Whether the transaction should be executed in read-only mode (disallows writes).</param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>
        /// A task that completes when the transaction is successfully committed; otherwise throws if all attempts fail
        /// or if the operation is cancelled.
        /// </returns>
        public static async Task Atomic<T>(
            Func<Transaction<T>, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            int attempt = 0;

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

                attempt++;

                // Conflict detected: wait before retrying
                int delay = BackoffPolicy.GetDelayMilliseconds(backoffType, attempt, initialBackoffMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }

            // All attempts failed: throw timeout exception
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }

        /// <summary>
        /// Executes a transactional action using the provided<see cref = "StmOptions" /> for retries, backoff and mode.
        ///  This overload accepts a synchronous action and internally wraps it into the asynchronous overload.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="Transaction{T}"/> within the user action.
        /// This is not a return type; the method completes when the transaction commits or throws on failure.
        /// </typeparam>
        /// <param name="action">User-defined synchronous action containing the transactional logic.</param>
        /// <param name="options">
        /// Configuration for retry policy, delays, backoff strategy and transaction mode. If <c>null</c>, <see cref="StmOptions.Default"/> is used.
        /// </param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>
        /// A task that completes when the transaction is successfully committed; otherwise it throws if all attempts fail
        /// or if the operation is cancelled.
        /// </returns>
        /// <remarks>
        /// - Honors read-only mode via <see cref="StmOptions.Mode"/>.<br/>
        /// - Uses the same retry and backoff semantics as the asynchronous overload.
        /// </remarks>
        public static Task Atomic<T>(
            Action<Transaction<T>> action,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            options ??= StmOptions.Default;
            return Atomic<T>(tx =>
            {
                action(tx);
                return Task.CompletedTask;
            }, options, cancellationToken);
        }

        /// <summary>Executes a transactional function using the provided<see cref = "StmOptions" /> for retries, backoff and mode.
        ///  Automatically retries on conflicts according to the configured policy.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="Transaction{T}"/> within the user function.
        /// This is not a return type; the method completes when the transaction commits or throws on failure.
        /// </typeparam>
        /// <param name="func">User-defined asynchronous function containing the transactional logic.</param>
        /// <param name="options">
        /// Configuration for retry policy, delays (<see cref="StmOptions.BaseDelay"/>, <see cref="StmOptions.MaxDelay"/>),
        /// backoff strategy (<see cref="StmOptions.Strategy"/>) and transaction mode (<see cref="StmOptions.Mode"/>).
        /// If <c>null</c>, <see cref="StmOptions.Default"/> is used.
        /// </param>
        /// <param name="cancellationToken">Token used to cancel the operation externally.</param>
        /// <returns>
        /// A task that completes when the transaction is successfully committed; otherwise it throws if all attempts fail
        /// or if the operation is cancelled.
        /// </returns>
        /// <exception cref="TimeoutException">
        /// Thrown when all attempts (see <see cref="StmOptions.MaxAttempts"/>) are exhausted without a successful commit.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown if <paramref name="cancellationToken"/> is signaled during execution.
        /// </exception>
        /// <remarks>
        /// Read-only transactions validate snapshots but never persist writes. Backoff delays are computed via
        /// <c>BackoffPolicy.GetDelayMilliseconds</c> using the configured strategy and delay bounds.
        /// </remarks>
        public static async Task Atomic<T>(
            Func<Transaction<T>, Task> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            int attempt = 0;

            while (attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var transaction = new Transaction<T>(isReadOnly);

                await func(transaction).ConfigureAwait(false);

                if (transaction.Commit())
                    return;

                attempt++;

                int delay = BackoffPolicy.GetDelayMilliseconds(strategy, attempt, baseMs, maxMs);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }
    }
}
