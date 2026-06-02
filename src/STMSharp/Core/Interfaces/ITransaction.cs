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

        /// <summary>
        /// Abandons the current attempt and blocks until one of the variables read so far
        /// is changed by another committed transaction, then re-executes the transaction
        /// from the start. This is condition synchronization without a lock and without
        /// busy-waiting: a consumer that finds nothing to consume calls <c>Retry</c> and is
        /// woken when a producer commits.
        ///
        /// Calling <c>Retry</c> with an empty read set would block forever, since nothing
        /// could ever wake it; that case throws <see cref="System.InvalidOperationException"/>.
        /// The transactional delegate must be free of irreversible side effects, because it
        /// will run again after the wake-up.
        /// </summary>
        void Retry();

        /// <summary>
        /// Composes two alternatives within the same transaction. Runs <paramref name="first"/>;
        /// if it completes without blocking, its effects stand and <paramref name="second"/> is
        /// not run. If <paramref name="first"/> blocks via <see cref="Retry"/>, its tentative
        /// writes are discarded and <paramref name="second"/> runs in their place. If
        /// <paramref name="second"/> also blocks, the whole composition blocks on the union of
        /// the variables read by both alternatives, so a change to any of them wakes it.
        ///
        /// Reads performed by a blocked first alternative are retained so the union is correct;
        /// only its writes are rolled back. A conflict-driven retry or a user exception in either
        /// alternative is not caught here and aborts or propagates as usual.
        /// </summary>
        void OrElse(Action<ITransaction> first, Action<ITransaction> second);
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
