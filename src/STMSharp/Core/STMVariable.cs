// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Runtime.CompilerServices;
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// A thread-safe STM variable that supports both reference and value types.
    /// 
    /// Semantics:
    /// - Writes are visible atomically and advance a monotonic version counter.
    /// - Readers can obtain a consistent (Value, Version) snapshot using ReadWithVersion().
    /// - Transactional commit uses internal CAS-based reservation helpers (no runtime locks).
    ///
    /// Notes on T:
    /// - If T is a mutable reference type, external mutations that bypass Write(...) can break isolation
    ///   because the version won't change. Prefer immutable types or treat T as a value.
    /// </summary>
    public class STMVariable<T> : ISTMVariable<T>
    {
        // Boxed value to support both value types and reference types
        private object _boxedValue;

        // Monotonic version; incremented on every successful write or reservation transition
        private long _version;

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
        /// Writes a new value and increments the version atomically
        /// (if the value actually changed by EqualityComparer).
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
        /// Current version of the variable (monotonic).
        /// </summary>
        public long Version => Volatile.Read(ref _version);

        /// <summary>
        /// Manually increments the version (e.g., for pessimistic schemes or forcing invalidation).
        /// </summary>
        public void IncrementVersion()
        {
            Interlocked.Increment(ref _version);
        }

        /// <summary>
        /// Returns a consistent snapshot of the value and its version.
        /// Ensures that the version did not change during the read.
        /// </summary>
        public (T Value, long Version) ReadWithVersion()
        {
            T value;
            long versionBefore, versionAfter;

            do
            {
                versionBefore = Version;
                value = Read();
                versionAfter = Version;
            }
            while (versionBefore != versionAfter);

            return (value, versionBefore);
        }

        // --------------------------------------------------------------------
        // Internal helpers for lock-free transactional commit (CAS-based)
        // --------------------------------------------------------------------

        // Only acquire if the expected snapshot version is EVEN and equals the current version.
        // This prevents "stealing" or advancing a version while someone else holds a reservation.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryAcquireForWrite(long expectedSnapshotVersion)
        {
            if ((expectedSnapshotVersion & 1L) != 0) return false; // must be even (unlocked)
            return Interlocked.CompareExchange(
                ref _version,
                expectedSnapshotVersion + 1,   // make it odd: reserved
                expectedSnapshotVersion        // expected even
            ) == expectedSnapshotVersion;
        }

        /// <summary>
        /// Writes the new value and releases the reservation by advancing version again.
        /// Must be called only after a successful TryAcquireForWrite on this instance.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteAndRelease(T value)
        {
            // Apply the new value first, then advance the version to release the reservation.
            Volatile.Write(ref _boxedValue, value!);
            Interlocked.Increment(ref _version); // from reserved to committed
        }

        /// <summary>
        /// Releases a previously acquired reservation without writing a new value.
        /// Used when the transaction aborts after acquiring reservations.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReleaseAfterAbort()
        {
            // Advance version to cancel the reservation (no value change).
            Interlocked.Increment(ref _version);
        }
    }
}