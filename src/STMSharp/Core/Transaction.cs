// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    // The class represents an STM transaction that manages STM variables
    // A generic STM transaction class that handles STM variables
    public class Transaction<T>
    {
        // Dictionaries to track reads, writes, and versions of STM variables of type T
        private readonly Dictionary<ISTMVariable<T>, object> _reads = [];
        private readonly Dictionary<ISTMVariable<T>, object> _writes = [];
        private readonly Dictionary<ISTMVariable<T>, int> _versions = [];

        public static int ConflictCount { get; private set; } = 0;
        public static int RetryCount { get; private set; } = 0;

        /// <summary>
        /// Reads a value from an STM variable.
        /// </summary>
        /// <param name="variable">The STM variable to read from.</param>
        /// <returns>The value of the variable.</returns>
        public T Read(ISTMVariable<T> variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            if (!_reads.TryGetValue(variable, out object? value))
            {
                value = variable.Read();
                _reads[variable] = value;
                _versions[variable] = variable.Version;
            }

            return (T)value;
        }

        /// <summary>
        /// Writes a value to an STM variable.
        /// </summary>
        /// <param name="variable">The STM variable to write to.</param>
        /// <param name="value">The value to write to the variable.</param>
        public void Write(ISTMVariable<T> variable, T value)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            _writes[variable] = value;
        }

        /// <summary>
        /// Checks for conflicts between reads and writes within the transaction.
        /// </summary>
        /// <returns>True if a conflict is detected, otherwise false.</returns>
        public bool CheckForConflicts()
        {
            foreach (var variable in _writes.Keys)
            {
                if (_versions.TryGetValue(variable, out int recordedVersion) &&
                    variable.Version != recordedVersion)
                {
                    ConflictCount++;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Commits the transaction and applies the writes to the STM variables.
        /// </summary>
        public bool Commit()
        {
            // Check for conflicts before committing
            if (CheckForConflicts())
            {
                RetryCount++;

                // If there's a conflict, return false without committing
                return false;
            }

            foreach (var entry in _writes)
            {
                var variable = entry.Key;

                // Write the value to the STM variable
                variable.Write((T)entry.Value);
            }

            return true;
        }
    }
}
