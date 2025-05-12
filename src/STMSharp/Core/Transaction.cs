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

        public static int ConflictCount { get; private set; } = 0;
        public static int RetryCount { get; private set; } = 0;

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
                    ConflictCount++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks for conflicts: ensures variables' versions are still locked.
        /// </summary>
        //public bool CheckForConflicts()
        //{
        //    foreach (var variable in _writes.Keys)
        //    {
        //        if (_lockedVersions.TryGetValue(variable, out int lockedVersion) &&
        //            variable.Version != lockedVersion)
        //        {
        //            ConflictCount++;
        //            return true; // Conflict detected: the version changed
        //        }
        //    }

        //    return false;
        //}

        /// <summary>
        /// Attempts to commit the transaction. Returns true if successful.
        /// </summary>
        public bool Commit()
        {
            if (CheckForConflicts())
            {
                RetryCount++;
                return false; // Abort due to conflict
            }

            // Commit writes to variables, once confirmed that no conflict occurred
            foreach (var entry in _writes)
            {
                var variable = entry.Key;
                var value = (T)entry.Value;

                variable.Write(value); // Apply the write to the STM variable
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
    }
}
