// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// A thread-safe STM variable that supports both reference and value types.
    /// </summary>
    public class STMVariable<T> : ISTMVariable<T>
    {
        // Boxed value to support both value types and reference types
        private object _boxedValue;
        private int _version;

        public STMVariable(T initialValue)
        {
            _boxedValue = initialValue!;
            _version = 0;
        }

        /// <summary>
        /// Reads the current value in a thread-safe manner.
        /// </summary>
        public T Read()
        {
            var value = Volatile.Read(ref _boxedValue);
            return (T)value!;
        }

        /// <summary>
        /// Writes a new value and increments the version atomically.
        /// </summary>
        public void Write(T value)
        {
            var currentValue = (T)Volatile.Read(ref _boxedValue)!;
            if (!EqualityComparer<T>.Default.Equals(currentValue, value))
            {
                Volatile.Write(ref _boxedValue, value!);
                Interlocked.Increment(ref _version);
            }
        }

        /// <summary>
        /// Returns the current version of the variable for transaction conflict checks.
        /// </summary>
        public int Version => Volatile.Read(ref _version);

        /// <summary>
        /// Manually increments the version (e.g., for pessimistic versioning).
        /// </summary>
        public void IncrementVersion()
        {
            Interlocked.Increment(ref _version);
        }

        /// <summary>
        /// Returns a consistent snapshot of the value and its version.
        /// Ensures that the version did not change during the read.
        /// </summary>
        public (T Value, int Version) ReadWithVersion()
        {
            T value;
            int versionBefore, versionAfter;

            do
            {
                versionBefore = Version;
                value = Read();
                versionAfter = Version;
            }
            while (versionBefore != versionAfter);

            return (value, versionBefore);
        }
    }
}