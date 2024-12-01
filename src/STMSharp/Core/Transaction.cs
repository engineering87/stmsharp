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

        /// <summary>
        /// Reads a value from an STM variable.
        /// </summary>
        /// <param name="variable">The STM variable to read from.</param>
        /// <returns>The value of the variable.</returns>
        public T Read(ISTMVariable<T> variable)
        {
            // If the variable has not been read yet, store its value
            if (!_reads.ContainsKey(variable))
            {
                // Read the value from the variable
                _reads[variable] = variable.Read();
                // Store the variable's version at the time of the read
                _versions[variable] = variable.Version;
            }

            // Return the stored value, cast to the correct type
            return (T)_reads[variable];
        }

        /// <summary>
        /// Writes a value to an STM variable.
        /// </summary>
        /// <param name="variable">The STM variable to write to.</param>
        /// <param name="value">The value to write to the variable.</param>
        public void Write(ISTMVariable<T> variable, T value)
        {
            // Add or update the value in the writes dictionary
            if (!_writes.ContainsKey(variable))
            {
                _writes[variable] = value;
            }
            else
            {
                _writes[variable] = value;
            }
        }

        /// <summary>
        /// Checks for conflicts between reads and writes within the transaction.
        /// </summary>
        /// <returns>True if a conflict is detected, otherwise false.</returns>
        public bool CheckForConflicts()
        {
            foreach (var entry in _writes)
            {
                var variable = entry.Key;

                // Access the current version of the variable
                var newVersion = variable.Version;

                if (_versions.ContainsKey(variable) && _versions[variable] != newVersion)
                {
                    // If the version has changed, there is a conflicts
                    return true;
                }
            }

            // No conflict found
            return false;
        }

        /// <summary>
        /// Commits the transaction and applies the writes to the STM variables.
        /// </summary>
        public void Commit()
        {
            foreach (var entry in _writes)
            {
                var variable = entry.Key;

                // Write the value to the STM variable
                variable.Write((T)entry.Value);
            }
        }
    }
}
