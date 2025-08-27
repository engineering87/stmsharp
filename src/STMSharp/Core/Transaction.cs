// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// Software Transactional Memory transaction with optimistic validation.
    /// - Read-your-own-writes: reads observe buffered writes within the same transaction.
    /// - Write-set validation: variables first seen via Write are included in validation.
    ///
    /// NOTE: No write-locks are taken here, so write-write races can still occur.
    ///       A deterministic write-set locking phase can be added later to prevent lost updates.
    /// </summary>
    public class Transaction<T>
    {
        // Stores values read during the transaction
        private readonly Dictionary<ISTMVariable<T>, T> _reads = [];

        // Stores values written during the transaction
        private readonly Dictionary<ISTMVariable<T>, T> _writes = [];

        // Tracks the locked versions of STM variables
        private readonly Dictionary<ISTMVariable<T>, int> _lockedVersions = [];

        // Internal global counters for conflicts and retries (thread-safe)
        private static int _conflictCount = 0;
        private static int _retryCount = 0;

        private readonly bool _isReadOnly;

        /// <summary>
        /// Gets the number of detected conflicts in a thread-safe manner.
        /// </summary>
        public static int ConflictCount => Volatile.Read(ref _conflictCount);

        /// <summary>
        /// Gets the number of retry attempts in a thread-safe manner.
        /// </summary>
        public static int RetryCount => Volatile.Read(ref _retryCount);

        public Transaction(bool isReadOnly = false)
        {
            _isReadOnly = isReadOnly;
        }

        /// <summary>
        /// Reads a value from an STM variable.
        /// - If the variable was written in this transaction, returns the buffered value (read-your-own-writes).
        /// - Otherwise, reads the current value and captures its version for later conflict detection.
        /// </summary>
        public T Read(ISTMVariable<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);

            // 1) Read-your-own-writes
            if (_writes.TryGetValue(variable, out var pending))
                return pending;

            // 2) Cached read
            if (_reads.TryGetValue(variable, out var cachedValue))
                return cachedValue;

            // 3) First access: take a consistent snapshot and remember its version
            var (value, version) = variable.ReadWithVersion();
            _reads[variable] = value;
            if (!_lockedVersions.ContainsKey(variable))
                _lockedVersions[variable] = version; // include in validation set
            return value;
        }

        /// <summary>
        /// Buffers a write to the STM variable (deferred until commit).
        /// Also ensures the variable is part of the validation set (write-set validation).
        /// </summary>
        public void Write(ISTMVariable<T> variable, T value)
        {
            ArgumentNullException.ThrowIfNull(variable);

            // Buffer the write
            _writes[variable] = value;

            // Ensure subsequent reads in this transaction see the new value
            _reads[variable] = value;

            // If this variable wasn't observed before, capture its current version
            // so that CheckForConflicts() also validates this write.
            if (!_lockedVersions.ContainsKey(variable))
            {
                var (_, version) = variable.ReadWithVersion();
                _lockedVersions[variable] = version;
            }
        }

        /// <summary>
        /// Checks for conflicts on all accessed STM variables (reads & writes)
        /// by comparing their current version against the version locked at read time.
        /// </summary>
        public bool CheckForConflicts()
        {
            foreach (var kvp in _lockedVersions)
            {
                ISTMVariable<T> variable = kvp.Key;
                int lockedVersion = kvp.Value;

                if (variable.Version != lockedVersion)
                {
                    // Increment conflict counter in a thread-safe way
                    Interlocked.Increment(ref _conflictCount);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to commit the transaction.
        /// Returns true if validation succeeds and writes are applied (if not read-only).
        ///
        /// NOTE: This is still an optimistic commit without write-locks, so concurrent
        /// write-write races can exist. In a subsequent step we can introduce a
        /// deterministic write-set locking protocol to eliminate lost updates.
        /// </summary>
        public bool Commit()
        {
            if (CheckForConflicts())
            {
                // Increment retry count if conflict occurred
                Interlocked.Increment(ref _retryCount);
                Clear(); 
                return false; // Abort due to conflict
            }

            // Skip commit phase if the transation is read only
            if (!_isReadOnly && _writes.Count > 0)
            {
                // Commit writes to variables, once confirmed that no conflict occurred
                foreach (var entry in _writes)
                {
                    var variable = entry.Key;
                    var value = entry.Value;

                    variable.Write(value); // Apply the write to the STM variable
                }
            }

            Clear();
            return true;
        }

        /// <summary>
        /// Clears internal state after commit or abort.
        /// </summary>
        private void Clear()
        {
            _reads.Clear();
            _writes.Clear();
            _lockedVersions.Clear();
        }

        /// <summary>
        /// Resets the global counters for conflicts and retries.
        /// </summary>
        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _conflictCount, 0);
            Interlocked.Exchange(ref _retryCount, 0);
        }
    }
}
