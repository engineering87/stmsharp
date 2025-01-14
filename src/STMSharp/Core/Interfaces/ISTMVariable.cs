// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Interface representing a transactional memory system.
    /// </summary>
    public interface ISTMVariable<T>
    {
        /// <summary>
        /// Reads the current value of the transactional variable.
        /// </summary>
        /// <returns>The current value of type <typeparamref name="T"/>.</returns>
        T Read();

        /// <summary>
        /// Writes a new value to the transactional variable.
        /// </summary>
        /// <param name="value">The new value of type <typeparamref name="T"/>.</param>
        void Write(T value);

        /// <summary>
        /// Gets the version of the transactional variable, used for concurrency control.
        /// </summary>
        int Version { get; }
    }
}
