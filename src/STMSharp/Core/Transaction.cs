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

        /// <summary>
        /// Attempts to commit. Returns false if the transaction must be retried.
        /// </summary>
        public bool Commit()
        {
            // Read-only or no writes: every read was validated against the start version,
            // so the observed snapshot is consistent and no commit-time work is required.
            if (_isReadOnly || _writeCount == 0)
                return true;

            // Acquire write-set locks in a deterministic total order (by Id) to avoid deadlock.
            Array.Sort(_writeVars!, _writeVals!, 0, _writeCount, IdComparer);

            // Number of write-set locks currently held (a prefix of the sorted write set).
            int locked = 0;
            long writeVersion = 0; // always overwritten by GlobalVersionClock.Next() before publish

            try
            {
                for (int i = 0; i < _writeCount; i++)
                {
                    if (!_writeVars![i].TryLock())
                        return ReleaseAndFail(locked);

                    locked++;
                }

                // Advance the clock to obtain this commit's write version.
                writeVersion = GlobalVersionClock.Next();

                // If no other commit happened between our start version and our write version,
                // the read set cannot have changed, so its validation can be skipped.
                if (writeVersion != _readVersion + 1)
                {
                    for (int i = 0; i < _readCount; i++)
                    {
                        var v = _readVars![i];
                        long word = v.VersionLockWord;

                        if (IsInWriteSet(v))
                        {
                            // Locked by us: ensure it was not committed by another writer
                            // between our read and our lock acquisition.
                            if (VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseAndFail(locked);
                        }
                        else
                        {
                            if (VersionLock.IsLocked(word) || VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseAndFail(locked);
                        }
                    }
                }
            }
            catch
            {
                // Unexpected failure before publishing: we still hold the first `locked` locks.
                for (int i = locked - 1; i >= 0; i--)
                {
                    try { _writeVars![i].Unlock(); } catch { /* best effort */ }
                }
                throw;
            }

            // Publish phase. From here we hold all locks and validation has passed.
            // PublishBoxed and UnlockWithVersion perform only volatile writes and do not throw.
            for (int i = 0; i < _writeCount; i++)
                _writeVars![i].PublishBoxed(_writeVals![i]);

            for (int i = 0; i < _writeCount; i++)
                _writeVars![i].UnlockWithVersion(writeVersion);

            // All locks released: now it is safe to wake any transactions blocked (via Retry)
            // on the variables this transaction changed. Done last so no STM lock is held.
            for (int i = 0; i < _writeCount; i++)
                _writeVars![i].SignalCommitted();

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

        private bool ReleaseAndFail(int locked)
        {
            for (int i = locked - 1; i >= 0; i--)
                _writeVars![i].Unlock();

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
