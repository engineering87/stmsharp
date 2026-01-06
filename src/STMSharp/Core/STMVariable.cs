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
    /// - Values are published atomically.
    /// - A monotonic version tracks changes and coordinates writers via an even/odd protocol.
    /// - Readers can obtain a consistent (Value, Version) snapshot using ReadWithVersion().
    /// - Transactional commit uses internal CAS-based reservation helpers (no runtime locks).
    ///
    /// Version protocol (even/odd):
    /// - Even version => variable is free (no writer reservation)
    /// - Odd  version => variable is reserved by a writer (commit attempt or direct write)
    ///
    /// Notes on T:
    /// - If T is a mutable reference type, external mutations that bypass Write(...) can break isolation
    ///   because the version won't change. Prefer immutable types or treat T as a value.
    /// </summary>
    public sealed class STMVariable<T>(T initialValue) : ISTMVariable<T>
    {
        // Unique, monotonic id used for deterministic ordering of write-set acquisition.
        // Static is per closed generic type STMVariable<T>, matching Transaction<T> usage.
        private static long _idSeq;
        internal long Id { get; } = Interlocked.Increment(ref _idSeq);

        // Boxed value to support both value types and reference types
        private object _boxedValue = initialValue!;

        // Monotonic version; also used for reservation (even/odd scheme)
        private long _version = 0;

        /// <summary>
        /// Reads the current value in a thread-safe manner.
        /// </summary>
        public T Read()
        {
            var value = Volatile.Read(ref _boxedValue);
            return (T)value!;
        }

        /// <summary>
        /// Writes a new value while preserving the internal even/odd version protocol:
        /// - Even version  => free
        /// - Odd version   => reserved by a writer
        ///
        /// This method performs a seqlock-style update:
        /// 1) Wait until the variable is not reserved (version is even)
        /// 2) Reserve the variable by CAS-ing version from even -> odd
        /// 3) Publish the new value
        /// 4) Release the reservation by incrementing version (odd -> even)
        ///
        /// This is a direct (non-transactional) write, but it is protocol-compatible and
        /// will not leave the variable stuck in a reserved (odd) state.
        /// </summary>
        public void Write(T value)
        {
            var spinner = new SpinWait();

            while (true)
            {
                // Read current version (volatile via property).
                // If odd, another writer currently holds a reservation.
                long v = Version;

                if ((v & 1L) != 0)
                {
                    spinner.SpinOnce();
                    continue;
                }

                // Optional fast-path: avoid reserving if the value would not change.
                var currentValue = (T)Volatile.Read(ref _boxedValue)!;
                if (EqualityComparer<T>.Default.Equals(currentValue, value))
                    return;

                // Reserve: even -> odd
                if (Interlocked.CompareExchange(ref _version, v + 1, v) != v)
                {
                    spinner.SpinOnce();
                    continue;
                }

                // Publish new value under our reservation.
                Volatile.Write(ref _boxedValue, value!);

                // Release: odd -> even
                Interlocked.Increment(ref _version);
                return;
            }
        }

        /// <summary>
        /// Current version of the variable (monotonic).
        /// </summary>
        public long Version => Volatile.Read(ref _version);

        /// <summary>
        /// Manually advances the version without changing the stored value.
        ///
        /// IMPORTANT:
        /// This method must NOT leave the version odd, because odd means "reserved".
        /// Therefore, it advances the version by 2 (even -> even) using CAS.
        /// </summary>
        public void IncrementVersion()
        {
            var spinner = new SpinWait();

            while (true)
            {
                long v = Version;

                // Do not interfere with a writer reservation.
                if ((v & 1L) != 0)
                {
                    spinner.SpinOnce();
                    continue;
                }

                // Keep parity even: add 2 using CAS.
                if (Interlocked.CompareExchange(ref _version, v + 2, v) == v)
                    return;

                spinner.SpinOnce();
            }
        }

        /// <summary>
        /// Returns a consistent snapshot of the value and its version.
        /// Ensures that the version did not change during the read.
        /// </summary>
        public (T Value, long Version) ReadWithVersion()
        {
            var spinner = new SpinWait();

            while (true)
            {
                long v1 = Version;

                // If version is odd, a writer has a reservation: retry.
                if ((v1 & 1L) != 0)
                {
                    spinner.SpinOnce();
                    continue;
                }

                T value = Read();
                long v2 = Version;

                // Valid snapshot: unchanged and even
                if (v1 == v2 && (v1 & 1L) == 0)
                    return (value, v1);

                spinner.SpinOnce();
            }
        }

        // --------------------------------------------------------------------
        // Internal helpers for lock-free transactional commit (CAS-based)
        // --------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryAcquireForWrite(long expectedSnapshotVersion)
        {
            if ((expectedSnapshotVersion & 1L) != 0) return false;

            return Interlocked.CompareExchange(
                ref _version,
                expectedSnapshotVersion + 1,   // reserved (odd)
                expectedSnapshotVersion        // expected (even)
            ) == expectedSnapshotVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteAndRelease(T value)
        {
            Volatile.Write(ref _boxedValue, value!);
            Interlocked.Increment(ref _version); // odd -> even
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReleaseAfterAbort()
        {
            Interlocked.Increment(ref _version); // odd -> even
        }
    }
}