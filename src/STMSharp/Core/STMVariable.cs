// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Runtime.CompilerServices;
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// A thread-safe STM variable that supports both reference and value types.
    ///
    /// Concurrency model (TL2-style):
    /// - Each variable holds a value and a versioned write-lock word (see <see cref="VersionLock"/>):
    ///   bit 0 is the lock flag and the remaining bits hold the version.
    /// - The version is a stamp drawn from the <see cref="GlobalVersionClock"/> at commit time,
    ///   so versions are comparable across all variables. This is what allows a transaction
    ///   to validate a consistent snapshot during its whole execution (opacity).
    /// - Transactional commits acquire the lock, stamp a new global version, and release it.
    ///   Direct writes follow the same lock protocol so they interoperate safely.
    ///
    /// Notes on T:
    /// - If T is a mutable reference type, external mutations that bypass Write(...) can break
    ///   isolation because the version will not change. Prefer immutable types or treat T as a value.
    ///
    /// Note on the public Version:
    /// - <see cref="Version"/> reports the commit version (a global stamp). It is monotonic but,
    ///   unlike earlier releases, it is not constrained to be even: the lock flag is held in a
    ///   separate bit and is not part of the reported version.
    /// </summary>
    public sealed class STMVariable<T> : IStmVariable
    {
        // Process-unique, monotonic id for deterministic write-set lock ordering.
        private static long _idSeq;
        private readonly long _id = Interlocked.Increment(ref _idSeq);

        // Boxed value to support both value types and reference types.
        // (De-boxing for value types is deferred to the performance pass.)
        private object? _boxedValue;

        // Versioned write-lock word: bit 0 = locked, remaining bits = version.
        private long _versionLock; // starts at 0 => version 0, unlocked

        // Lazily-allocated set of wake handles for transactions blocked on this variable
        // via ITransaction.Retry. Null until the first waiter registers, so variables that
        // are never waited on pay nothing. Guarded by its own lock for register/unregister/signal.
        private HashSet<System.Threading.ManualResetEventSlim>? _waiters;
        private readonly object _waitersGate = new();

        public STMVariable(T initialValue)
        {
            _boxedValue = initialValue;
        }

        /// <summary>
        /// Reads the latest published value in a thread-safe manner (no version check).
        /// </summary>
        public T Read() => (T)Volatile.Read(ref _boxedValue)!;

        /// <summary>
        /// Current commit version of the variable (monotonic global stamp).
        /// </summary>
        public long Version => VersionLock.VersionOf(Volatile.Read(ref _versionLock));

        /// <summary>
        /// Direct, non-transactional write that follows the same lock protocol as the
        /// transactional commit, so it interoperates safely with concurrent transactions.
        /// A write that does not change the value does not advance the version.
        /// </summary>
        public void Write(T value)
        {
            var spinner = new SpinWait();

            while (true)
            {
                long word = Volatile.Read(ref _versionLock);

                if (VersionLock.IsLocked(word))
                {
                    spinner.SpinOnce();
                    continue;
                }

                // Reserve: even (unlocked) -> odd (locked)
                if (Interlocked.CompareExchange(ref _versionLock, word | 1L, word) != word)
                {
                    spinner.SpinOnce();
                    continue;
                }

                // Lock held: no other writer can publish concurrently.
                var current = (T)Volatile.Read(ref _boxedValue)!;
                if (EqualityComparer<T>.Default.Equals(current, value))
                {
                    // No change: release the lock without advancing the version.
                    Volatile.Write(ref _versionLock, word);
                    return;
                }

                Volatile.Write(ref _boxedValue, value);
                Volatile.Write(ref _versionLock, VersionLock.Stamp(GlobalVersionClock.Next()));
                SignalWaiters();
                return;
            }
        }

        /// <summary>
        /// Advances the version to a new global stamp without changing the stored value.
        /// </summary>
        public void IncrementVersion()
        {
            var spinner = new SpinWait();

            while (true)
            {
                long word = Volatile.Read(ref _versionLock);

                if (VersionLock.IsLocked(word))
                {
                    spinner.SpinOnce();
                    continue;
                }

                if (Interlocked.CompareExchange(ref _versionLock, word | 1L, word) != word)
                {
                    spinner.SpinOnce();
                    continue;
                }

                Volatile.Write(ref _versionLock, VersionLock.Stamp(GlobalVersionClock.Next()));
                return;
            }
        }

        /// <summary>
        /// Returns a consistent snapshot of the value and its commit version,
        /// retrying while a writer holds the lock or the version changes mid-read.
        /// </summary>
        public (T Value, long Version) ReadWithVersion()
        {
            var spinner = new SpinWait();

            while (true)
            {
                long w1 = Volatile.Read(ref _versionLock);

                if (VersionLock.IsLocked(w1))
                {
                    spinner.SpinOnce();
                    continue;
                }

                var value = (T)Volatile.Read(ref _boxedValue)!;
                long w2 = Volatile.Read(ref _versionLock);

                if (w1 == w2)
                    return (value, VersionLock.VersionOf(w1));

                spinner.SpinOnce();
            }
        }

        // --------------------------------------------------------------------
        // Transactional read used by the engine (TL2 post-validated read).
        // --------------------------------------------------------------------

        /// <summary>
        /// Reads the value with a version consistent with the given start version.
        /// Returns false if the location is locked, changed during the read, or is
        /// newer than the snapshot; the caller must then abort and retry.
        /// </summary>
        internal bool TryReadForTransaction(long readVersion, out T value, out long versionWord)
        {
            long w1 = Volatile.Read(ref _versionLock);

            if (VersionLock.IsLocked(w1))
            {
                value = default!;
                versionWord = w1;
                return false;
            }

            var observed = (T)Volatile.Read(ref _boxedValue)!;
            long w2 = Volatile.Read(ref _versionLock);

            if (w1 != w2 || VersionLock.VersionOf(w1) > readVersion)
            {
                value = default!;
                versionWord = w1;
                return false;
            }

            value = observed;
            versionWord = w1;
            return true;
        }

        // --------------------------------------------------------------------
        // IStmVariable: heterogeneous commit hooks used by the transaction engine.
        // --------------------------------------------------------------------

        long IStmVariable.Id => _id;

        long IStmVariable.VersionLockWord => Volatile.Read(ref _versionLock);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IStmVariable.TryLock()
        {
            long word = Volatile.Read(ref _versionLock);
            if (VersionLock.IsLocked(word))
                return false;

            return Interlocked.CompareExchange(ref _versionLock, word | 1L, word) == word;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IStmVariable.UnlockWithVersion(long version)
            => Volatile.Write(ref _versionLock, VersionLock.Stamp(version));

        // Explicit post-commit wake-up, called by the committer after ALL write-set locks
        // have been released, so no STM lock is held while waiters are signaled.
        void IStmVariable.SignalCommitted() => SignalWaiters();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IStmVariable.Unlock()
        {
            long word = Volatile.Read(ref _versionLock);
            Volatile.Write(ref _versionLock, word & ~1L);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void IStmVariable.PublishBoxed(object? boxedValue)
            => Volatile.Write(ref _boxedValue, boxedValue);

        // --------------------------------------------------------------------
        // Wait registry for ITransaction.Retry (condition synchronization).
        // --------------------------------------------------------------------

        void IStmVariable.RegisterWaiter(System.Threading.ManualResetEventSlim waiter)
        {
            lock (_waitersGate)
            {
                _waiters ??= new HashSet<System.Threading.ManualResetEventSlim>();
                _waiters.Add(waiter);
            }
        }

        void IStmVariable.UnregisterWaiter(System.Threading.ManualResetEventSlim waiter)
        {
            lock (_waitersGate)
            {
                _waiters?.Remove(waiter);
            }
        }

        // Wakes every transaction currently blocked on this variable. Best effort: it only
        // sets the handles, it never runs user code, and it is called after the new version
        // has been published, so a woken transaction re-reads a committed, consistent state.
        private void SignalWaiters()
        {
            HashSet<System.Threading.ManualResetEventSlim>? snapshot;
            lock (_waitersGate)
            {
                if (_waiters is null || _waiters.Count == 0)
                    return;

                // Copy under the lock, then set outside it, so a waiter unregistering during
                // wake-up cannot mutate the set we are iterating.
                snapshot = new HashSet<System.Threading.ManualResetEventSlim>(_waiters);
            }

            foreach (var w in snapshot)
            {
                try { w.Set(); } catch (ObjectDisposedException) { /* waiter already gone */ }
            }
        }

    }
}
