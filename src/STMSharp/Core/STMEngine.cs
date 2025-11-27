// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Backoff;
using STMSharp.Core.Interfaces;
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
    /// Typical usage:
    /// <code>
    /// var shared = new STMVariable<int>(0);
    ///
    /// await STMEngine.Atomic<int>(tx =>
    /// {
    ///     var value = tx.Read(shared);
    ///     tx.Write(shared, value + 1);
    /// });
    /// </code>
    /// </summary>
    public static class STMEngine
    {
        private const int DefaultMaxAttempts = 3;
        private const int DefaultInitialBackoffMilliseconds = 100;
        private const BackoffType DefaultBackoffType = BackoffType.ExponentialWithJitter;

        /// <summary>
        /// Executes a transactional action with automatic retries in case of conflict.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="STMVariable{T}"/> and managed by the transactional context.
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
        public static Task Atomic<T>(
            Action<ITransaction<T>> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            // Wrap the synchronous action into the asynchronous overload
            return Atomic<T>(
                tx =>
                {
                    action(tx);
                    return Task.CompletedTask;
                },
                maxAttempts,
                initialBackoffMilliseconds,
                backoffType,
                readOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes an asynchronous transactional function with automatic retries in case of conflict.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="STMVariable{T}"/> and managed by the transactional context.
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
            Func<ITransaction<T>, Task> func,
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

                // Create a new transaction instance for each attempt (internal implementation)
                var transaction = new Transaction<T>(readOnly);

                // Execute user-provided transactional logic
                await func(transaction).ConfigureAwait(false);

                // Attempt to commit: returns true if no conflict detected
                if (transaction.Commit())
                {
                    // Commit successful, exit method
                    return;
                }

                attempt++;

                // Conflict detected: wait before retrying
                int delay = BackoffPolicy.GetDelayMilliseconds(backoffType, attempt, initialBackoffMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            // All attempts failed: throw timeout exception
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }

        /// <summary>
        /// Executes a transactional action using the provided <see cref="StmOptions"/> for retries, backoff and mode.
        /// This overload accepts a synchronous action and internally wraps it into the asynchronous overload.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="STMVariable{T}"/> and managed by the transactional context.
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
            Action<ITransaction<T>> action,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            options ??= StmOptions.Default;

            return Atomic<T>(
                tx =>
                {
                    action(tx);
                    return Task.CompletedTask;
                },
                options,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional function using the provided <see cref="StmOptions"/> for retries, backoff and mode.
        /// Automatically retries on conflicts according to the configured policy.
        /// </summary>
        /// <typeparam name="T">
        /// The STM value type used by <see cref="STMVariable{T}"/> and managed by the transactional context.
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
        /// <see cref="BackoffPolicy.GetDelayMilliseconds(BackoffType,int,int,int)"/> using the configured strategy and delay bounds.
        /// </remarks>
        public static async Task Atomic<T>(
            Func<ITransaction<T>, Task> func,
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