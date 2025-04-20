// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)

namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Interface representing a transactional memory variable.
    /// Restricted to internal STM operations only.
    /// </summary>
    public interface ISTMVariable<T>
    {
        /// <summary>
        /// Reads the current value and its version atomically.
        /// </summary>
        /// <returns>A tuple containing the value and its version.</returns>
        (T Value, int Version) ReadWithVersion();

        /// <summary>
        /// Writes a new value atomically to the STM variable.
        /// </summary>
        /// <param name="value">The new value to write.</param>
        void Write(T value);

        /// <summary>
        /// Gets the current version of the variable.
        /// </summary>
        int Version { get; }

        /// <summary>
        /// Increments the version of the variable.
        /// </summary>
        void IncrementVersion();
    }
}