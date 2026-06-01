// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Backoff;
using STMSharp.Core.Exceptions;
using STMSharp.Core.Interfaces;
using STMSharp.Enum;

namespace STMSharp.Core
{
    /// <summary>
    /// STMEngine executes atomic operations within Software Transactional Memory using
    /// a TL2-style protocol with automatic retries and a configurable backoff strategy.
    ///
    /// A single transaction can read and write <see cref="STMVariable{T}"/> instances of
    /// any element type via the non-generic <see cref="ITransaction"/>. Reads see a
    /// consistent snapshot for the whole transaction; an inconsistent read aborts and the
    /// delegate is retried. When the retry budget is exhausted the engine throws
    /// <see cref="TransactionConflictException"/>.
    ///
    /// The transactional delegate may be retried, so it must be free of irreversible
    /// side effects (or make them idempotent).
    ///
    /// Typical usage:
    /// <code>
    /// var balance = new STMVariable&lt;int&gt;(0);
    /// var name    = new STMVariable&lt;string&gt;("");
    ///
    /// await STMEngine.Atomic(tx =>
    /// {
    ///     tx.Write(balance, tx.Read(balance) + 10);
    ///     tx.Write(name, "updated");
    /// });
    /// </code>
    /// </summary>
    public static class STMEngine
    {
        private const int DefaultMaxAttempts = 3;
        private const int DefaultInitialBackoffMilliseconds = 100;
        private const int DefaultMaxBackoffMilliseconds = 2000;
        private const BackoffType DefaultBackoffType = BackoffType.ExponentialWithJitter;

        // =====================================================================
        // Non-generic API (preferred): one transaction, heterogeneous variables.
        // =====================================================================

        public static Task Atomic(
            Action<ITransaction> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync<object?>(
                tx => { action(tx); return Task.FromResult<object?>(null); },
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

        public static Task Atomic(
            Func<ITransaction, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync<object?>(
                async tx => { await func(tx).ConfigureAwait(false); return null; },
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

        public static Task Atomic(
            Action<ITransaction> action,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync<object?>(
                tx => { action(tx); return Task.FromResult<object?>(null); },
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        public static Task Atomic(
            Func<ITransaction, Task> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync<object?>(
                async tx => { await func(tx).ConfigureAwait(false); return null; },
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        // Value-returning transactions take an asynchronous body, Func<ITransaction, Task<TResult>>.
        // A synchronous-result overload, Func<ITransaction, TResult>, is intentionally not provided:
        // having both is ambiguous for any lambda that returns a Task, because the compiler cannot
        // decide between TResult = Task<...> and an asynchronous body. For a synchronous transaction
        // that produces a value, capture the value with the void overload, for example:
        //     int balance = 0;
        //     await STMEngine.Atomic(tx => { balance = tx.Read(account); });
        public static Task<TResult> Atomic<TResult>(
            Func<ITransaction, Task<TResult>> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds,
            int maxBackoffMilliseconds = DefaultMaxBackoffMilliseconds,
            BackoffType backoffType = DefaultBackoffType,
            bool readOnly = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync(
                func,
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

        public static Task<TResult> Atomic<TResult>(
            Func<ITransaction, Task<TResult>> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync(
                func,
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        // =====================================================================
        // Legacy single-type API (delegates to the core via LegacyTransactionView).
        // =====================================================================

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
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync<object?>(
                tx => { action(new LegacyTransactionView<T>(tx)); return Task.FromResult<object?>(null); },
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

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
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync<object?>(
                async tx => { await func(new LegacyTransactionView<T>(tx)).ConfigureAwait(false); return null; },
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

        public static Task Atomic<T>(
            Action<ITransaction<T>> action,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync<object?>(
                tx => { action(new LegacyTransactionView<T>(tx)); return Task.FromResult<object?>(null); },
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        public static Task Atomic<T>(
            Func<ITransaction<T>, Task> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync<object?>(
                async tx => { await func(new LegacyTransactionView<T>(tx)).ConfigureAwait(false); return null; },
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

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
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync(
                tx => Task.FromResult(func(new LegacyTransactionView<T>(tx))),
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

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
            ValidateBudget(maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds);

            return RunCoreAsync(
                tx => func(new LegacyTransactionView<T>(tx)),
                maxAttempts, initialBackoffMilliseconds, maxBackoffMilliseconds,
                backoffType, readOnly, cancellationToken);
        }

        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, TResult> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync(
                tx => Task.FromResult(func(new LegacyTransactionView<T>(tx))),
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        public static Task<TResult> Atomic<T, TResult>(
            Func<ITransaction<T>, Task<TResult>> func,
            StmOptions? options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(func);
            options ??= StmOptions.Default;
            var (maxAttempts, baseMs, maxMs, strategy, isReadOnly) = options.ToPolicyArgs();

            return RunCoreAsync(
                tx => func(new LegacyTransactionView<T>(tx)),
                maxAttempts, baseMs, maxMs, strategy, isReadOnly, cancellationToken);
        }

        // =====================================================================
        // Shared retry/backoff loop. All public Atomic overloads route here.
        // =====================================================================

        private static async Task<TResult> RunCoreAsync<TResult>(
            Func<StmTransaction, Task<TResult>> body,
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

                var transaction = new StmTransaction(isReadOnly);
                bool aborted = false;
                TResult result = default!;

                try
                {
                    result = await body(transaction).ConfigureAwait(false);
                }
                catch (TransactionRetryException)
                {
                    // Snapshot became inconsistent mid-execution; retry the whole delegate.
                    aborted = true;
                }

                if (!aborted && transaction.Commit())
                    return result;

                StmTransaction.IncrementConflict();
                StmTransaction.IncrementRetry();

                attempt++;
                if (attempt >= maxAttempts)
                    break;

                int delay = BackoffPolicy.GetDelayMilliseconds(strategy, attempt, baseMs, maxMs);
                if (delay > 0)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            StmTransaction.IncrementUnresolvedConflictCount();
            throw new TransactionConflictException(
                $"STM transaction failed to commit after {maxAttempts} attempt(s) due to repeated conflicts.");
        }

        private static void ValidateBudget(int maxAttempts, int initialBackoffMilliseconds, int maxBackoffMilliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
            ArgumentOutOfRangeException.ThrowIfNegative(initialBackoffMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(maxBackoffMilliseconds);
        }
    }
}
