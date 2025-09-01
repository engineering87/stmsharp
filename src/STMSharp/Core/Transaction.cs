// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;
using System.Runtime.CompilerServices;

namespace STMSharp.Core
{
    /// <summary>
    /// Software Transactional Memory transaction with optimistic read snapshots
    /// and a lock-free CAS-based commit protocol (reserve → revalidate → write&release).
    ///
    /// Properties:
    /// - Read-your-own-writes: reads observe buffered writes within the same transaction.
    /// - Immutable snapshot: the first observation of each variable (read or write-first)
    ///   captures a version used for validation and reservation; it is never refreshed.
    /// - Commit uses version CAS to prevent write-write lost updates without runtime locks.
    ///
    /// Notes:
    /// - Conflict and retry counters are static per closed generic type (Transaction&lt;T&gt;).
    /// - Read-only transactions validate but never persist writes.
    /// </summary>
    public class Transaction<T>
    {
        // Read cache and read-your-own-writes within this transaction
        private readonly Dictionary<ISTMVariable<T>, T> _reads = [];

        // Buffered writes to be applied at commit time
        private readonly Dictionary<ISTMVariable<T>, T> _writes = [];

        // Immutable snapshot: for each observed variable, the version seen at first observation
        private readonly Dictionary<ISTMVariable<T>, long> _snapshotVersions = [];

        // Global-ish counters (per closed generic type)
        private static int _conflictCount = 0; // number of detected conflicts
        private static int _retryCount = 0;    // number of failed attempts (that required a retry)

        private readonly bool _isReadOnly;

        /// <summary>
        /// Total number of detected conflicts (thread-safe read).
        /// </summary>
        public static int ConflictCount => Volatile.Read(ref _conflictCount);

        /// <summary>
        /// Total number of retry attempts (thread-safe read).
        /// </summary>
        public static int RetryCount => Volatile.Read(ref _retryCount);

        public Transaction(bool isReadOnly = false)
        {
            _isReadOnly = isReadOnly;
        }

        /// <summary>
        /// Reads a value from an STM variable.
        /// - If written in this transaction, returns the buffered value (read-your-own-writes).
        /// - Else takes a consistent (Value, Version) snapshot; the version is recorded
        ///   once per variable and never refreshed for the lifetime of this transaction.
        /// </summary>
        public T Read(ISTMVariable<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);

            // Read-your-own-writes
            if (_writes.TryGetValue(variable, out var pending))
                return pending;

            // Cached read
            if (_reads.TryGetValue(variable, out var cached))
                return cached;

            // First observation: capture value and immutable snapshot version
            var (value, version) = variable.ReadWithVersion();
            _reads[variable] = value;
            if (!_snapshotVersions.ContainsKey(variable))
                _snapshotVersions[variable] = version;
            return value;
        }

        /// <summary>
        /// Buffers a write to the STM variable (deferred until commit) and guarantees
        /// read-your-own-writes semantics. Captures the snapshot version only if this is
        /// the first observation of the variable.
        /// </summary>
        public void Write(ISTMVariable<T> variable, T value)
        {
            ArgumentNullException.ThrowIfNull(variable);

            if (_isReadOnly)
                throw new InvalidOperationException("Cannot Write in a read-only transaction.");

            // Buffer the write and ensure subsequent reads see it
            _writes[variable] = value;
            _reads[variable] = value;

            // Capture immutable snapshot on first observation only
            if (!_snapshotVersions.ContainsKey(variable))
            {
                var (_, version) = variable.ReadWithVersion();
                _snapshotVersions[variable] = version;
            }
        }

        /// <summary>
        /// Validates the immutable snapshot: for each observed variable, the current version
        /// must still match the captured snapshot version. Returns true if a conflict is found.
        /// Increments the conflict counter once per failing call.
        /// </summary>
        public bool CheckForConflicts()
        {
            foreach (var kvp in _snapshotVersions)
            {
                var variable = kvp.Key;
                var snapshotVersion = kvp.Value;

                if (variable.Version != snapshotVersion)
                {
                    Interlocked.Increment(ref _conflictCount);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Attempts to commit the transaction.
        /// Returns true if validation succeeds and (for non read-only) writes are applied.
        ///
        /// Lock-free CAS protocol:
        /// 0) Ensure every write-set variable has an immutable snapshot (defensive).
        /// 1) Reserve each write-set variable by CAS-ing its version from snapshot to snapshot+1.
        /// 2) Re-validate the entire observed snapshot (read- and write-sets).
        /// 3) Apply buffered writes and release reservations by advancing version again.
        /// </summary>
        public bool Commit()
        {
            // Fast path: read-only or no writes → snapshot validation only
            if (_isReadOnly || _writes.Count == 0)
            {
                if (CheckForConflicts())
                {
                    Interlocked.Increment(ref _retryCount);
                    // CheckForConflicts() already bumped _conflictCount
                    Clear();
                    return false;
                }
                Clear();
                return true;
            }

            // 0) Defensive: every write must have a snapshot captured at first observation
            foreach (var w in _writes.Keys)
            {
                if (!_snapshotVersions.ContainsKey(w))
                {
                    Interlocked.Increment(ref _retryCount);
                    Interlocked.Increment(ref _conflictCount);
                    Clear();
                    return false;
                }
            }

            // 1) Reserve all write-set variables using a stable ordering
            //    Drive strictly by the write-set to avoid any set/key drift.
            var writeKeys = _writes.Keys
                .OrderBy(k => RuntimeHelpers.GetHashCode(k))
                .ToArray();
            var acquired = new List<STMVariable<T>>(writeKeys.Length);

            foreach (var w in _writes.Keys)
            {
                var snapVersion = _snapshotVersions[w];

                // Must be the concrete STMVariable<T> to use CAS helpers
                if (w is not STMVariable<T> concrete)
                {
                    Interlocked.Increment(ref _retryCount);
                    Interlocked.Increment(ref _conflictCount);
                    Clear();
                    return false;
                }

                // Try to reserve according to the immutable snapshot
                if (!concrete.TryAcquireForWrite(snapVersion))
                {
                    // Release any prior reservations and abort
                    foreach (STMVariable<T> c in acquired)
                        c.ReleaseAfterAbort();

                    Interlocked.Increment(ref _retryCount);
                    Interlocked.Increment(ref _conflictCount);
                    Clear();
                    return false;
                }

                acquired.Add(concrete);
            }

            // 2) Re-validate the entire observed snapshot (read-set + write-set).
            foreach (var kvp in _snapshotVersions)
            {
                var variable = kvp.Key;
                var snapVersion = kvp.Value;

                if (_writes.ContainsKey(variable))
                    continue; // write-set entries are already reserved by us

                // Must still equal the snapshot and must not be reserved by someone else
                var cur = variable.Version;
                if (cur != snapVersion || (cur & 1) != 0)
                {
                    foreach (STMVariable<T> c in acquired)
                        c.ReleaseAfterAbort();

                    Interlocked.Increment(ref _retryCount);
                    Interlocked.Increment(ref _conflictCount);
                    Clear();
                    return false;
                }
            }

            // 3) Apply writes and release reservations (advance version again)
            foreach (var kvp in _writes)
            {
                var variable = kvp.Key;
                var value = kvp.Value;

                ((STMVariable<T>)variable).WriteAndRelease(value);
            }

            Clear();
            return true;
        }

        /// <summary>
        /// Clears per-transaction state after commit or abort.
        /// </summary>
        private void Clear()
        {
            _reads.Clear();
            _writes.Clear();
            _snapshotVersions.Clear();
        }

        /// <summary>
        /// Resets global counters (per closed generic type).
        /// </summary>
        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _conflictCount, 0);
            Interlocked.Exchange(ref _retryCount, 0);
        }
    }
}