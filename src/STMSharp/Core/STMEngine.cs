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
    /// var shared = new STMVariable&lt;int&gt;(0);
    ///
    /// await STMEngine.Atomic&lt;int&gt;(tx =&gt;
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
        private const int DefaultMaxBackoffMilliseconds = 2000;
        private const BackoffType DefaultBackoffType = BackoffType.ExponentialWithJitter;

        /// <summary>
        /// Executes a transactional action with automatic retries in case of conflict.
        /// </summary>
        public static Task Atomic<T>(
            Action<ITransaction<T>> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);

            return Atomic<T>(
                tx =>
                {
                    action(tx);
                    return Task.CompletedTask;
                },
                maxAttempts,
                initialBackoffMilliseconds,
                maxBackoffMilliseconds,
                backoffType,
                readOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes an asynchronous transactional function with automatic retries in case of conflict.
        /// </summary>
        public static Task Atomic<T>(
            Func<ITransaction<T>, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
            ArgumentOutOfRangeException.ThrowIfNegative(initialBackoffMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(maxBackoffMilliseconds);

            return RunWithRetryAsync<T, object?>(
                async tx => { await func(tx).ConfigureAwait(false); return null; },
                maxAttempts,
                initialBackoffMilliseconds,
                maxBackoffMilliseconds,
                backoffType,
                readOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional action using the provided <see cref="StmOptions"/>.
        /// </summary>
        public static Task Atomic<T>(
            Action<ITransaction<T>> action,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);

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
        /// Executes an asynchronous transactional function using the provided <see cref="StmOptions"/>.
        /// </summary>
        public static Task Atomic<T>(
            Func<ITransaction<T>, Task> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);

            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunWithRetryAsync<T, object?>(
                async tx => { await func(tx).ConfigureAwait(false); return null; },
                maxAttempts,
                baseMs,
                maxMs,
                strategy,
                isReadOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional synchronous function that returns a result, with automatic retries.
        /// </summary>
        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, TResult> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
            ArgumentOutOfRangeException.ThrowIfNegative(initialBackoffMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(maxBackoffMilliseconds);

            return RunWithRetryAsync<T, TResult>(
                tx => Task.FromResult(func(tx)),
                maxAttempts,
                initialBackoffMilliseconds,
                maxBackoffMilliseconds,
                backoffType,
                readOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional asynchronous function that returns a result, with automatic retries.
        /// </summary>
        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, Task<TResult>> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
            ArgumentOutOfRangeException.ThrowIfNegative(initialBackoffMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(maxBackoffMilliseconds);

            return RunWithRetryAsync<T, TResult>(
                func,
                maxAttempts,
                initialBackoffMilliseconds,
                maxBackoffMilliseconds,
                backoffType,
                readOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional synchronous function returning a result, using <see cref="StmOptions"/>.
        /// </summary>
        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, TResult> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);

            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunWithRetryAsync<T, TResult>(
                tx => Task.FromResult(func(tx)),
                maxAttempts,
                baseMs,
                maxMs,
                strategy,
                isReadOnly,
                cancellationToken);
        }

        /// <summary>
        /// Executes a transactional asynchronous function returning a result, using <see cref="StmOptions"/>.
        /// </summary>
        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, Task<TResult>> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);

            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunWithRetryAsync<T, TResult>(
                func,
                maxAttempts,
                baseMs,
                maxMs,
                strategy,
                isReadOnly,
                cancellationToken);
        }

        // ---------------------------------------------------------------------
        // Shared retry/backoff loop. All public Atomic overloads route here.
        // ---------------------------------------------------------------------
        private static async Task<TResult> RunWithRetryAsync<T, TResult>(
            Func<ITransaction<T>, Task<TResult>> func,
            int maxAttempts,
            int baseMs,
            int maxMs,
            BackoffType strategy,
            bool isReadOnly,
            CancellationToken cancellationToken)
        {
            int attempt = 0;

            while (attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var transaction = new Transaction<T>(isReadOnly);

                TResult result = await func(transaction).ConfigureAwait(false);

                if (transaction.Commit())
                    return result;

                attempt++;

                // No need to wait after the final failed attempt: we're about to throw.
                if (attempt >= maxAttempts)
                    break;

                int delay = BackoffPolicy.GetDelayMilliseconds(strategy, attempt, baseMs, maxMs);
                if (delay > 0)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            Transaction<T>.IncrementUnresolvedConflictCount();
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }
    }
}
