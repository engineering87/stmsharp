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

        // Initial retries are absorbed by a CPU-level spin rather than a timed delay.
        // STM conflicts usually clear within microseconds, so the first retries must not
        // pay the operating-system timer quantum that Task.Delay incurs (about 15 ms on
        // Windows for any sub-quantum value). Only sustained contention reaches the timed ladder.
        private const int SpinRetries = 4;

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
                catch (TransactionBlockedException)
                {
                    // The delegate called Retry(): block until a read-set variable changes,
                    // then re-execute. This does not consume the conflict budget, because the
                    // transaction is waiting on a condition, not losing a race.
                    await BlockOnReadSetAsync(transaction, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!aborted && transaction.Commit())
                    return result;

                StmTransaction.IncrementConflict();
                StmTransaction.IncrementRetry();

                attempt++;
                if (attempt >= maxAttempts)
                    break;

                await BackoffAsync(strategy, attempt, baseMs, maxMs, cancellationToken).ConfigureAwait(false);
            }

            StmTransaction.IncrementUnresolvedConflictCount();
            throw new TransactionConflictException(
                $"STM transaction failed to commit after {maxAttempts} attempt(s) due to repeated conflicts.");
        }

        // Two-phase backoff. The first SpinRetries attempts back off with a bounded CPU spin
        // and a single cooperative yield, both on the microsecond scale and free of any timer.
        // Only beyond that does the configured timed ladder apply, so genuine sustained
        // contention still yields the thread and backs off in real time.
        private static Task BackoffAsync(
            BackoffType strategy, int attempt, int baseMs, int maxMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt <= SpinRetries)
            {
                // Bounded busy-spin that grows with the attempt but stays well under one
                // millisecond (about 128 to 1024 iterations), followed by one yield so a
                // competing transaction can win the contended slot.
                Thread.SpinWait(64 << attempt);
                Thread.Yield();
                return Task.CompletedTask;
            }

            // Sustained contention: timed ladder, offset so it restarts from its base
            // once the spin phase is over.
            int delay = BackoffPolicy.GetDelayMilliseconds(strategy, attempt - SpinRetries, baseMs, maxMs);
            return delay > 0
                ? Task.Delay(delay, cancellationToken)
                : Task.CompletedTask;
        }

        // Maximum time a Retry() blocks before re-executing anyway. This is a safety valve:
        // if a wake-up is ever missed, a blocked transaction degrades to a slow retry instead
        // of hanging forever. It does not change semantics on a correct wake-up path.
        private const int RetryBlockTimeoutMilliseconds = 1000;

        // Parks the transaction on its read set until a committer signals one of those
        // variables, or the safety-valve timeout elapses. The recheck after registration
        // closes the lost-wake-up window: if the read set already changed, do not park.
        private static async Task BlockOnReadSetAsync(StmTransaction transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: Retry() already rejects an empty read set, but never park on nothing.
            if (transaction.ReadCount == 0)
                return;

            using var waiter = new ManualResetEventSlim(initialState: false);
            transaction.RegisterWaiters(waiter);
            try
            {
                // Close the lost-wake-up window: if a relevant commit landed between the
                // Retry() call and the registration above, the read set already shows it,
                // so re-execute immediately rather than waiting for a signal that has passed.
                if (transaction.ReadSetChangedSinceStart())
                    return;

                // Park the wait on a pooled thread so the async flow is not blocked. The
                // timeout is the safety valve, not the expected wake path; a normal wake-up
                // sets the handle well before it. ManualResetEventSlim.Wait observes the
                // cancellation token and returns false on timeout.
                await Task.Run(() =>
                {
                    try { waiter.Wait(RetryBlockTimeoutMilliseconds, cancellationToken); }
                    catch (OperationCanceledException) { /* cancellation surfaces to the caller below */ }
                }, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                transaction.UnregisterWaiters(waiter);
            }
        }

        private static void ValidateBudget(int maxAttempts, int initialBackoffMilliseconds, int maxBackoffMilliseconds)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAttempts, 0);
            ArgumentOutOfRangeException.ThrowIfNegative(initialBackoffMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegative(maxBackoffMilliseconds);
        }
    }
}