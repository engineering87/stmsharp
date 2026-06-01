// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Transactional context passed to <c>STMEngine.Atomic</c>. A single transaction
    /// can read and write <see cref="STMVariable{T}"/> instances of any element type,
    /// because the read and write methods are generic per call rather than per context.
    ///
    /// Thread-safety: an instance is not thread-safe. The transactional delegate must
    /// run its Read/Write calls on a single logical flow; sharing one context across
    /// concurrent threads corrupts the internal read and write sets. Concurrency is
    /// provided across distinct transactions, not within one.
    /// </summary>
    public interface ITransaction
    {
        /// <summary>
        /// Reads a variable within the transaction, returning the pending value if the
        /// variable was already written in this transaction (read-your-own-writes).
        /// </summary>
        T Read<T>(STMVariable<T> variable);

        /// <summary>
        /// Buffers a write to a variable. The write is applied atomically at commit.
        /// </summary>
        void Write<T>(STMVariable<T> variable, T value);
    }

    /// <summary>
    /// Legacy single-type transactional context retained for source compatibility.
    /// Prefer the non-generic <see cref="ITransaction"/>, which lets one transaction
    /// span variables of different element types.
    /// </summary>
    public interface ITransaction<T>
    {
        T Read(STMVariable<T> variable);
        void Write(STMVariable<T> variable, T value);
    }
}
