// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Exceptions;
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// Software Transactional Memory transaction implementing a TL2-style protocol.
    ///
    /// On start the transaction samples a read version from the <see cref="GlobalVersionClock"/>.
    /// Every read is validated against that version, so the transaction always observes a
    /// consistent snapshot during execution (opacity); an inconsistent read aborts and retries.
    /// On commit a read-write transaction locks its write set in a deterministic order, advances
    /// the global clock to obtain a write version, revalidates its read set, then publishes the
    /// buffered values and stamps the new version. Read-only transactions commit with no extra work.
    ///
    /// The read set and write set are held in append-only buffers with linear-scan lookup rather
    /// than dictionaries. For the small transactions that are typical of STM this allocates far
    /// less and is faster than hashing; lookups are O(n) in the set size, so very large sets pay
    /// a quadratic cost. A dictionary fallback above a size threshold can be added later if needed.
    ///
    /// Thread-safety: an instance is not thread-safe; see <see cref="ITransaction"/>.
    /// </summary>
    internal sealed class StmTransaction : ITransaction
    {
        private const int InitialCapacity = 4;

        // Deterministic lock ordering by variable Id, cached once to avoid per-commit allocation.
        private static readonly IComparer<IStmVariable> IdComparer =
            Comparer<IStmVariable>.Create(static (a, b) => a.Id.CompareTo(b.Id));

        // Read set: the distinct variables read by the transaction. The observed version is not
        // stored because commit revalidates against the live version-lock word.
        private IStmVariable[]? _readVars;
        private int _readCount;

        // Write set: parallel buffers of variables and their pending (boxed) values.
        private IStmVariable[]? _writeVars;
        private object?[]? _writeVals;
        private int _writeCount;

        // Commute set: variables with a pending commutative operation applied to the live
        // committed value at commit time, under lock. The operation is erased to
        // Func<object?, object?> so heterogeneous element types share one buffer. A variable
        // present here is NOT validated as a read, which is what lets commuting updates avoid
        // conflicting with one another.
        private IStmVariable[]? _commuteVars;
        private Func<object?, object?>[]? _commuteOps;
        private int _commuteCount;

        private readonly bool _isReadOnly;
        private readonly long _readVersion;

        private static int _conflictCount;
        private static int _retryCount;
        private static int _unresolvedConflictCount;

        public StmTransaction(bool isReadOnly = false)
        {
            _isReadOnly = isReadOnly;
            _readVersion = GlobalVersionClock.Read();
        }

        public T Read<T>(STMVariable<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);

            // If this variable has a pending commute, materialize it into the write set first,
            // so the commute set and write set stay disjoint and the read observes the effect.
            MaterializeCommuteIfPending(variable);

            // Read-your-own-writes: return the pending value if already written in this transaction.
            for (int i = 0; i < _writeCount; i++)
            {
                if (ReferenceEquals(_writeVars![i], variable))
                    return (T)_writeVals![i]!;
            }

            if (!variable.TryReadForTransaction(_readVersion, out var value, out _))
                throw new TransactionRetryException();

            AddRead(variable);
            return value;
        }

        public void Write<T>(STMVariable<T> variable, T value)
        {
            ArgumentNullException.ThrowIfNull(variable);

            if (_isReadOnly)
                throw new InvalidOperationException("Cannot Write in a read-only transaction.");

            // A pending commute on this variable is superseded by an explicit write; drop it so
            // the commute set and write set remain disjoint. The write below records the value.
            DropPendingCommute(variable);

            // Boxes value types; de-boxing the write set is a separate, later step.
            for (int i = 0; i < _writeCount; i++)
            {
                if (ReferenceEquals(_writeVars![i], variable))
                {
                    _writeVals![i] = value;
                    return;
                }
            }

            if (_writeVars is null)
            {
                _writeVars = new IStmVariable[InitialCapacity];
                _writeVals = new object?[InitialCapacity];
            }
            else if (_writeCount == _writeVars.Length)
            {
                int n = _writeVars.Length * 2;
                Array.Resize(ref _writeVars, n);
                Array.Resize(ref _writeVals, n);
            }

            _writeVars![_writeCount] = variable;
            _writeVals![_writeCount] = value;
            _writeCount++;
        }

        public void Retry()
        {
            // Blocking with an empty read set could never be woken: reject it explicitly
            // rather than park forever.
            if (_readCount == 0)
                throw new InvalidOperationException(
                    "Retry() requires at least one prior Read; an empty read set could never be woken.");

            throw new TransactionBlockedException();
        }

        public void OrElse(Action<ITransaction> first, Action<ITransaction> second)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);

            // Checkpoint the write set only. If the first alternative blocks, its tentative
            // writes are rolled back to this point, but its reads are kept so that, if the
            // second alternative also blocks, the transaction blocks on the union of both
            // read sets (Haskell-STM orElse semantics). A conflict-driven retry or a user
            // exception is not caught here and propagates as usual.
            int writeCheckpoint = _writeCount;

            try
            {
                first(this);
                return; // first completed without blocking: its effects stand
            }
            catch (TransactionBlockedException)
            {
                // Discard the first alternative's tentative writes; keep its reads.
                TruncateWritesTo(writeCheckpoint);
            }

            // Run the second alternative in place of the first. If it blocks, the
            // TransactionBlockedException propagates to the engine, which parks on the
            // current read set (first's reads plus second's reads = the union).
            second(this);
        }

        // Rolls the write set back to a previous count, used by OrElse to discard a blocked
        // alternative's tentative writes. Clears the dropped slots so they do not pin values.
        private void TruncateWritesTo(int count)
        {
            for (int i = count; i < _writeCount; i++)
            {
                _writeVars![i] = null!;
                _writeVals![i] = null;
            }
            _writeCount = count;
        }

        public void Commute<T>(STMVariable<T> variable, Func<T, T> operation)
        {
            ArgumentNullException.ThrowIfNull(variable);
            ArgumentNullException.ThrowIfNull(operation);

            if (_isReadOnly)
                throw new InvalidOperationException("Cannot Commute in a read-only transaction.");

            // Conservative fallback: if the variable is already read or written
            // non-commutatively in this transaction, the commutative relaxation is unsafe
            // (the transaction has observed or fixed a concrete value), so apply the operation
            // eagerly through the normal write path, which is validated and published as usual.
            if (IsInWriteSet(variable) || IsInReadSet(variable))
            {
                Write(variable, operation(Read(variable)));
                return;
            }

            // If this variable already has a pending commute, compose the operations so the
            // buffer holds a single function. Composition order does not matter for genuinely
            // commutative operations, which is the precondition the caller must satisfy.
            for (int i = 0; i < _commuteCount; i++)
            {
                if (ReferenceEquals(_commuteVars![i], variable))
                {
                    var existing = _commuteOps![i];
                    _commuteOps![i] = boxed => operation((T)existing(boxed)!);
                    return;
                }
            }

            if (_commuteVars is null)
            {
                _commuteVars = new IStmVariable[InitialCapacity];
                _commuteOps = new Func<object?, object?>[InitialCapacity];
            }
            else if (_commuteCount == _commuteVars.Length)
            {
                int n = _commuteVars.Length * 2;
                Array.Resize(ref _commuteVars, n);
                Array.Resize(ref _commuteOps, n);
            }

            // Erase the typed operation to operate on the boxed value.
            _commuteVars![_commuteCount] = variable;
            _commuteOps![_commuteCount] = boxed => operation((T)boxed!);
            _commuteCount++;
        }

        private bool IsInReadSet(IStmVariable v)
        {
            for (int i = 0; i < _readCount; i++)
            {
                if (ReferenceEquals(_readVars![i], v))
                    return true;
            }
            return false;
        }

        // If the variable has a pending commute, apply it eagerly to the value read now and
        // record the result as a normal write, then remove the commute entry. This keeps the
        // commute set disjoint from the write set and moves the variable onto the validated path.
        private void MaterializeCommuteIfPending<T>(STMVariable<T> variable)
        {
            for (int i = 0; i < _commuteCount; i++)
            {
                if (ReferenceEquals(_commuteVars![i], variable))
                {
                    var op = _commuteOps![i];
                    RemoveCommuteAt(i);
                    // Read the committed value via the normal transactional read (adds it to the
                    // read set and validates the snapshot), then buffer the applied result.
                    Write(variable, (T)op(variable.Read())!);
                    return;
                }
            }
        }

        private void DropPendingCommute(IStmVariable variable)
        {
            for (int i = 0; i < _commuteCount; i++)
            {
                if (ReferenceEquals(_commuteVars![i], variable))
                {
                    RemoveCommuteAt(i);
                    return;
                }
            }
        }

        private void RemoveCommuteAt(int index)
        {
            // Compact by moving the last entry into the removed slot (order does not matter).
            int last = _commuteCount - 1;
            _commuteVars![index] = _commuteVars![last];
            _commuteOps![index] = _commuteOps![last];
            _commuteVars![last] = null!;
            _commuteOps![last] = null!;
            _commuteCount = last;
        }

        /// <summary>
        /// Attempts to commit. Returns false if the transaction must be retried.
        /// </summary>
        public bool Commit()
        {
            // Read-only: no writes and no commutes are possible, snapshot already validated.
            if (_isReadOnly)
                return true;

            // No writes and no commutes: every read was validated against the start version,
            // so the observed snapshot is consistent and no commit-time work is required.
            if (_writeCount == 0 && _commuteCount == 0)
                return true;

            // Build a single, Id-sorted lock plan over the union of the write set and the
            // commute set. Locking everything in one deterministic total order preserves
            // deadlock freedom exactly as for write-only commits. The two sets are disjoint:
            // Commute(...) routes any variable already written to the normal write path.
            int lockTargetCount = _writeCount + _commuteCount;
            var lockTargets = new IStmVariable[lockTargetCount];
            for (int i = 0; i < _writeCount; i++)
                lockTargets[i] = _writeVars![i];
            for (int i = 0; i < _commuteCount; i++)
                lockTargets[_writeCount + i] = _commuteVars![i];
            Array.Sort(lockTargets, 0, lockTargetCount, IdComparer);

            int locked = 0;
            long writeVersion = 0; // always overwritten by GlobalVersionClock.Next() before publish

            try
            {
                for (int i = 0; i < lockTargetCount; i++)
                {
                    // Acquire each lock in the deterministic Id order, waiting rather than
                    // aborting on contention. The total Id order makes waiting deadlock-free:
                    // two committers cannot hold-and-wait in a cycle, and the committer holding
                    // the lowest-Id locks is never blocked, so global progress is guaranteed.
                    // This removes the spurious aborts that pure physical lock contention used
                    // to cause, which is what let a commute-only transaction (no read set to
                    // validate, so no real conflict) exhaust its retry budget under many threads.
                    var spinner = new SpinWait();
                    while (!lockTargets[i].TryLock())
                        spinner.SpinOnce();

                    locked++;
                }

                // Advance the clock to obtain this commit's write version.
                writeVersion = GlobalVersionClock.Next();

                // Validate the read set. Commute-only variables are deliberately NOT in the
                // read set, so they are not validated here: that is what lets commuting
                // updates avoid conflicting with one another. A variable that was read
                // non-commutatively is validated as usual, which preserves serializability.
                // The read-set skip optimization is unsafe once commutes are present, because
                // a commute advances a variable's version without that variable being a
                // read-set entry, so we cannot infer "no intervening commit" from the clock.
                if (writeVersion != _readVersion + 1 || _commuteCount > 0)
                {
                    for (int i = 0; i < _readCount; i++)
                    {
                        var v = _readVars![i];
                        long word = v.VersionLockWord;

                        if (IsInWriteSet(v))
                        {
                            if (VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseTargetsAndFail(lockTargets, locked);
                        }
                        else
                        {
                            if (VersionLock.IsLocked(word) || VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseTargetsAndFail(lockTargets, locked);
                        }
                    }
                }
            }
            catch
            {
                for (int i = locked - 1; i >= 0; i--)
                {
                    try { lockTargets[i].Unlock(); } catch { /* best effort */ }
                }
                throw;
            }

            // Publish phase. We hold every lock and validation has passed.
            // Plain writes publish their buffered value.
            for (int i = 0; i < _writeCount; i++)
                _writeVars![i].PublishBoxed(_writeVals![i]);

            // Commutes read the live committed value under lock and apply their operation,
            // so two commuting updates compose correctly regardless of commit order.
            for (int i = 0; i < _commuteCount; i++)
            {
                var current = _commuteVars![i].ReadBoxed();
                _commuteVars![i].PublishBoxed(_commuteOps![i](current));
            }

            // Stamp the new version on every locked variable and release.
            for (int i = 0; i < lockTargetCount; i++)
                lockTargets[i].UnlockWithVersion(writeVersion);

            // All locks released: wake any transactions blocked (via Retry) on the variables
            // this transaction changed. Done last so no STM lock is held.
            for (int i = 0; i < lockTargetCount; i++)
                lockTargets[i].SignalCommitted();

            return true;
        }

        private bool IsInWriteSet(IStmVariable v)
        {
            for (int i = 0; i < _writeCount; i++)
            {
                if (ReferenceEquals(_writeVars![i], v))
                    return true;
            }
            return false;
        }

        private static bool ReleaseTargetsAndFail(IStmVariable[] lockTargets, int locked)
        {
            for (int i = locked - 1; i >= 0; i--)
                lockTargets[i].Unlock();

            return false;
        }

        private void AddRead(IStmVariable v)
        {
            // De-duplicate so repeated reads of the same variable do not grow the set.
            for (int i = 0; i < _readCount; i++)
            {
                if (ReferenceEquals(_readVars![i], v))
                    return;
            }

            if (_readVars is null)
                _readVars = new IStmVariable[InitialCapacity];
            else if (_readCount == _readVars.Length)
                Array.Resize(ref _readVars, _readVars.Length * 2);

            _readVars![_readCount++] = v;
        }

        // --------------------------------------------------------------------
        // Blocking support for Retry, used by the engine. These run between attempts,
        // on the engine's flow, never concurrently with this transaction's own Read/Write.
        // --------------------------------------------------------------------

        /// <summary>Number of distinct variables read, exposed so the engine can detect
        /// the degenerate empty-read-set case before parking.</summary>
        internal int ReadCount => _readCount;

        /// <summary>Registers the wake handle against every variable in the read set.</summary>
        internal void RegisterWaiters(System.Threading.ManualResetEventSlim waiter)
        {
            for (int i = 0; i < _readCount; i++)
                _readVars![i].RegisterWaiter(waiter);
        }

        /// <summary>Removes the wake handle from every variable in the read set.</summary>
        internal void UnregisterWaiters(System.Threading.ManualResetEventSlim waiter)
        {
            for (int i = 0; i < _readCount; i++)
                _readVars![i].UnregisterWaiter(waiter);
        }

        /// <summary>
        /// Returns true if any read-set variable has already advanced past this transaction's
        /// start version, or is currently locked by a committer. The engine calls this after
        /// registering waiters but before parking: if it returns true the engine must not park,
        /// because the change it would have waited for may have already happened, which is how
        /// the lost-wake-up window is closed.
        /// </summary>
        internal bool ReadSetChangedSinceStart()
        {
            for (int i = 0; i < _readCount; i++)
            {
                long word = _readVars![i].VersionLockWord;
                if (VersionLock.IsLocked(word) || VersionLock.VersionOf(word) > _readVersion)
                    return true;
            }
            return false;
        }

        public static int ConflictCount => Volatile.Read(ref _conflictCount);
        public static int RetryCount => Volatile.Read(ref _retryCount);
        public static int UnresolvedConflictCount => Volatile.Read(ref _unresolvedConflictCount);

        internal static void IncrementConflict() => Interlocked.Increment(ref _conflictCount);
        internal static void IncrementRetry() => Interlocked.Increment(ref _retryCount);
        internal static void IncrementUnresolvedConflictCount() => Interlocked.Increment(ref _unresolvedConflictCount);

        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _conflictCount, 0);
            Interlocked.Exchange(ref _retryCount, 0);
            Interlocked.Exchange(ref _unresolvedConflictCount, 0);
        }
    }

    /// <summary>
    /// Adapter exposing a <see cref="StmTransaction"/> through the legacy single-type
    /// <see cref="ITransaction{T}"/> surface, for source compatibility.
    /// </summary>
    internal sealed class LegacyTransactionView<T>(StmTransaction transaction) : ITransaction<T>
    {
        public T Read(STMVariable<T> variable) => transaction.Read(variable);
        public void Write(STMVariable<T> variable, T value) => transaction.Write(variable, value);
    }
}
