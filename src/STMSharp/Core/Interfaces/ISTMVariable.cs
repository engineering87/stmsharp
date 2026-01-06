// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Interface representing a transactional memory variable used internally by STM.
    /// </summary>
    internal interface ISTMVariable<T>
    {
        /// <summary>
        /// Reads the current value and its version atomically.
        /// </summary>
        /// <returns>A tuple containing the value and its version.</returns>
        (T Value, long Version) ReadWithVersion();

        /// <summary>
        /// Gets the current version of the variable.
        /// </summary>
        long Version { get; }
    }
}