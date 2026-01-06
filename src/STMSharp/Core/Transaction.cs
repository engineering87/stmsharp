// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// Software Transactional Memory transaction with optimistic read snapshots
    /// and a lock-free CAS-based commit protocol (reserve → revalidate → write&release).
    /// </summary>
    internal class Transaction<T>(bool isReadOnly = false) : ITransaction<T>
    {
        // Track variables by reference identity (avoid surprises with custom Equals/GetHashCode).
        private readonly Dictionary<ISTMVariable<T>, T> _reads =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        private readonly Dictionary<ISTMVariable<T>, T> _writes =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        private readonly Dictionary<ISTMVariable<T>, long> _snapshotVersions =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        private static int _conflictCount;
        private static int _retryCount;

        private readonly bool _isReadOnly = isReadOnly;

        public static int ConflictCount => Volatile.Read(ref _conflictCount);
        public static int RetryCount => Volatile.Read(ref _retryCount);

        public T Read(ISTMVariable<T> variable)
        {
            ArgumentNullException.ThrowIfNull(variable);

            if (_writes.TryGetValue(variable, out var pending))
                return pending;

            if (_reads.TryGetValue(variable, out var cached))
                return cached;

            var (value, version) = variable.ReadWithVersion();
            _reads[variable] = value;

            if (!_snapshotVersions.ContainsKey(variable))
                _snapshotVersions[variable] = version;

            return value;
        }

        public void Write(ISTMVariable<T> variable, T value)
        {
            ArgumentNullException.ThrowIfNull(variable);

            if (_isReadOnly)
                throw new InvalidOperationException("Cannot Write in a read-only transaction.");

            _writes[variable] = value;
            _reads[variable] = value;

            if (!_snapshotVersions.ContainsKey(variable))
            {
                var (_, version) = variable.ReadWithVersion();
                _snapshotVersions[variable] = version;
            }
        }

        public bool CheckForConflicts()
        {
            foreach (var kvp in _snapshotVersions)
            {
                var variable = kvp.Key;
                var snapshotVersion = kvp.Value;

                if (variable.Version != snapshotVersion)
                {
                    Interlocked.Increment(ref _conflictCount);
                    return true;
                }
            }

            return false;
        }

        public bool Commit()
        {
            if (_isReadOnly || _writes.Count == 0)
            {
                if (CheckForConflicts())
                {
                    Interlocked.Increment(ref _retryCount);
                    Clear();
                    return false;
                }

                Clear();
                return true;
            }

            foreach (var w in _writes.Keys)
            {
                if (!_snapshotVersions.ContainsKey(w))
                {
                    Interlocked.Increment(ref _retryCount);
                    Interlocked.Increment(ref _conflictCount);
                    Clear();
                    return false;
                }
            }

            // Deterministic ordering: acquire reservations by per-variable unique Id (total order).
            var writeKeys = _writes.Keys
                .Select(v => v as STMVariable<T>)
                .ToArray();

            // Defensive: should never happen because ISTMVariable<T> is internal and STMVariable<T> is the implementation.
            if (writeKeys.Any(v => v is null))
            {
                Interlocked.Increment(ref _retryCount);
                Interlocked.Increment(ref _conflictCount);
                Clear();
                return false;
            }

            Array.Sort(writeKeys!, (a, b) => a!.Id.CompareTo(b!.Id));

            var acquired = new List<STMVariable<T>>(writeKeys.Length);

            bool AbortWithRelease()
            {
                for (int i = acquired.Count - 1; i >= 0; i--)
                    acquired[i].ReleaseAfterAbort();

                Interlocked.Increment(ref _retryCount);
                Interlocked.Increment(ref _conflictCount);
                Clear();
                return false;
            }

            try
            {
                foreach (var w in writeKeys!)
                {
                    var snapVersion = _snapshotVersions[w];

                    if (!w!.TryAcquireForWrite(snapVersion))
                        return AbortWithRelease();

                    acquired.Add(w);
                }

                foreach (var kvp in _snapshotVersions)
                {
                    var variable = kvp.Key;
                    var snapVersion = kvp.Value;

                    if (_writes.ContainsKey(variable))
                        continue;

                    var curVersion = variable.Version;

                    if (curVersion != snapVersion || (curVersion & 1L) != 0)
                        return AbortWithRelease();
                }

                foreach (var w in writeKeys!)
                {
                    w!.WriteAndRelease(_writes[w]);
                }

                Clear();
                return true;
            }
            catch
            {
                for (int i = acquired.Count - 1; i >= 0; i--)
                {
                    try { acquired[i].ReleaseAfterAbort(); } catch { }
                }

                Clear();
                throw;
            }
        }

        private void Clear()
        {
            _reads.Clear();
            _writes.Clear();
            _snapshotVersions.Clear();
        }

        public static void ResetCounters()
        {
            Interlocked.Exchange(ref _conflictCount, 0);
            Interlocked.Exchange(ref _retryCount, 0);
        }

        T ITransaction<T>.Read(STMVariable<T> variable) => Read(variable);
        void ITransaction<T>.Write(STMVariable<T> variable, T value) => Write(variable, value);
    }
}