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
    /// Thread-safety: an instance is not thread-safe; see <see cref="ITransaction"/>.
    /// </summary>
    internal sealed class StmTransaction : ITransaction
    {
        // Tracked by reference identity (independent of any custom Equals/GetHashCode).
        private readonly Dictionary<IStmVariable, long> _reads =
            new(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<IStmVariable, object?> _writes =
            new(ReferenceEqualityComparer.Instance);

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

            // Read-your-own-writes.
            if (_writes.TryGetValue(variable, out var pending))
                return (T)pending!;

            if (!variable.TryReadForTransaction(_readVersion, out var value, out var word))
                throw new TransactionRetryException();

            _reads[variable] = word;
            return value;
        }

        public void Write<T>(STMVariable<T> variable, T value)
        {
            ArgumentNullException.ThrowIfNull(variable);

            if (_isReadOnly)
                throw new InvalidOperationException("Cannot Write in a read-only transaction.");

            // Boxes value types; de-boxing is deferred to the performance pass.
            _writes[variable] = value;
        }

        /// <summary>
        /// Attempts to commit. Returns false if the transaction must be retried.
        /// </summary>
        public bool Commit()
        {
            // Read-only or no writes: every read was validated against the start version,
            // so the observed snapshot is consistent and no commit-time work is required.
            if (_isReadOnly || _writes.Count == 0)
                return true;

            // Acquire write-set locks in a deterministic total order (by Id) to avoid deadlock.
            var writeVars = new IStmVariable[_writes.Count];
            _writes.Keys.CopyTo(writeVars, 0);
            Array.Sort(writeVars, static (a, b) => a.Id.CompareTo(b.Id));

            var acquired = new List<IStmVariable>(writeVars.Length);
            long writeVersion = 0; // always overwritten by GlobalVersionClock.Next() before publish

            try
            {
                foreach (var v in writeVars)
                {
                    if (!v.TryLock())
                        return ReleaseAndFail(acquired);

                    acquired.Add(v);
                }

                // Advance the clock to obtain this commit's write version.
                writeVersion = GlobalVersionClock.Next();

                // If no other commit happened between our start version and our write version,
                // the read set cannot have changed, so its validation can be skipped.
                if (writeVersion != _readVersion + 1)
                {
                    foreach (var kvp in _reads)
                    {
                        var v = kvp.Key;
                        long word = v.VersionLockWord;

                        if (_writes.ContainsKey(v))
                        {
                            // Locked by us: ensure it was not committed by another writer
                            // between our read and our lock acquisition.
                            if (VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseAndFail(acquired);
                        }
                        else
                        {
                            if (VersionLock.IsLocked(word) || VersionLock.VersionOf(word) > _readVersion)
                                return ReleaseAndFail(acquired);
                        }
                    }
                }
            }
            catch
            {
                // Unexpected failure before publishing: we still hold every acquired lock.
                for (int i = acquired.Count - 1; i >= 0; i--)
                {
                    try { acquired[i].Unlock(); } catch { /* best effort */ }
                }
                throw;
            }

            // Publish phase. From here we hold all locks and validation has passed.
            // PublishBoxed and UnlockWithVersion perform only volatile writes and do not throw.
            foreach (var v in writeVars)
                v.PublishBoxed(_writes[v]);

            foreach (var v in writeVars)
                v.UnlockWithVersion(writeVersion);

            return true;
        }

        private bool ReleaseAndFail(List<IStmVariable> acquired)
        {
            for (int i = acquired.Count - 1; i >= 0; i--)
                acquired[i].Unlock();

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
