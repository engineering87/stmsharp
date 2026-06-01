// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Non-generic view of an STM variable used by the transaction engine to track,
    /// lock, validate, and publish variables of heterogeneous element types within a
    /// single transaction. <see cref="STMVariable{T}"/> is the only implementation.
    ///
    /// The element value is type-erased at this boundary: the write set carries the
    /// pending value as a boxed object and the variable unboxes it during publish.
    /// Eliminating that boxing for value types is deferred to the performance pass.
    /// </summary>
    internal interface IStmVariable
    {
        /// <summary>
        /// Stable, process-unique identifier used to acquire write-set locks in a
        /// deterministic total order, which prevents deadlock and reduces livelock.
        /// </summary>
        long Id { get; }

        /// <summary>
        /// Current versioned-lock word read atomically (see <see cref="VersionLock"/>).
        /// </summary>
        long VersionLockWord { get; }

        /// <summary>
        /// Attempts to acquire the write lock with a single compare-and-swap.
        /// Returns false if the variable is already locked or was concurrently changed.
        /// </summary>
        bool TryLock();

        /// <summary>
        /// Releases the lock and stamps the given version (clears the lock bit).
        /// Must be called only by the lock holder.
        /// </summary>
        void UnlockWithVersion(long version);

        /// <summary>
        /// Releases the lock without changing the stored version (abort path).
        /// Must be called only by the lock holder.
        /// </summary>
        void Unlock();

        /// <summary>
        /// Publishes the pending (boxed) value while the variable is locked by the committer.
        /// </summary>
        void PublishBoxed(object? boxedValue);
    }
}
