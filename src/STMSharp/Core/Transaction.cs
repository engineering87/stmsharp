// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// Represents a Software Transactional Memory transaction.
    /// Implements pessimistic versioning, blocking variable versions until commit.
    /// </summary>
    public class Transaction<T>
    {
        // Stores values read during the transaction
        private readonly Dictionary<ISTMVariable<T>, object> _reads = new();

        // Stores values written during the transaction
        private readonly Dictionary<ISTMVariable<T>, object> _writes = new();

        // Tracks the locked versions of STM variables
        private readonly Dictionary<ISTMVariable<T>, int> _lockedVersions = new();

        // Internal counters for conflicts and retries (thread-safe)
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
        /// Locks the variable version for pessimistic isolation.
        /// </summary>
        public T Read(ISTMVariable<T> variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            if (!_reads.TryGetValue(variable, out object? cachedValue))
            {
                // Read the value and the version, then lock the version for pessimistic isolation
                var (value, version) = variable.ReadWithVersion();

                _reads[variable] = value!;
                _lockedVersions[variable] = version; // Lock the version

                return value;
            }

            return (T)cachedValue!;
        }

        /// <summary>
        /// Writes a value to the STM variable (deferred until commit).
        /// </summary>
        public void Write(ISTMVariable<T> variable, T value)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            _writes[variable] = value!;
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
        /// Attempts to commit the transaction. Returns true if successful.
        /// </summary>
        public bool Commit()
        {
            if (CheckForConflicts())
            {
                // Increment retry count if conflict occurred
                Interlocked.Increment(ref _retryCount);
                return false; // Abort due to conflict
            }

            // Skip commit phase if the transation is read only
            if (!_isReadOnly)
            {
                // Commit writes to variables, once confirmed that no conflict occurred
                foreach (var entry in _writes)
                {
                    var variable = entry.Key;
                    var value = (T)entry.Value;

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
            ResetCounters();
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
