// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core.Collections
{
    /// <summary>
    /// A transactional dictionary with fine-grained value concurrency.
    ///
    /// Each present key owns its own <see cref="STMVariable{TValue}"/> value cell, so two
    /// transactions that update the values of different existing keys never conflict. Membership,
    /// the set of keys, is governed by a single structural variable holding an immutable directory
    /// snapshot; insertion and removal go through that variable and are therefore validated, which
    /// prevents phantom reads (a transaction that observed a key's absence aborts if another
    /// transaction inserts that key and commits first). The design is intentionally split: values
    /// are fine-grained, structure is coarse. Two structural mutations, or a structural mutation and
    /// any operation that observed the directory, conflict with one another.
    ///
    /// All operations take an <see cref="ITransaction"/> and compose inside <c>STMEngine.Atomic</c>,
    /// so several dictionary operations within one transaction commit atomically as a unit.
    ///
    /// Consistency cost: a structural mutation copies the directory, so it is O(n) in the number of
    /// keys, whereas a value update on an existing key is O(1). A per-bucket structural scheme that
    /// would let insertions of different keys proceed without conflict is deliberately out of scope
    /// here, because it requires per-bucket phantom handling that cannot be validated safely without
    /// a multi-threaded test pass.
    ///
    /// Thread-safety: an instance is safe to share across transactions. A published directory
    /// snapshot is never mutated after publication, so concurrent readers of a snapshot are safe.
    /// </summary>
    public sealed class TransactionalDictionary<TKey, TValue> where TKey : notnull
    {
        /// <summary>
        /// Holds a key's value in its own versioned cell, so value updates are independent per key.
        /// </summary>
        private sealed class Entry
        {
            public readonly STMVariable<TValue> Cell;

            public Entry(TValue value) => Cell = new STMVariable<TValue>(value);
        }

        /// <summary>
        /// An immutable snapshot of the key set. The backing map is never mutated after the
        /// constructor returns; structural changes build a new snapshot by copy-on-write. This
        /// invariant is what makes concurrent reads of a published snapshot safe.
        /// </summary>
        private sealed class Directory
        {
            public readonly Dictionary<TKey, Entry> Map;

            public Directory(Dictionary<TKey, Entry> map) => Map = map;

            public static readonly Directory Empty = new(new Dictionary<TKey, Entry>());
        }

        private readonly STMVariable<Directory> _directory;

        public TransactionalDictionary()
        {
            _directory = new STMVariable<Directory>(Directory.Empty);
        }

        public TransactionalDictionary(IEqualityComparer<TKey> comparer)
        {
            ArgumentNullException.ThrowIfNull(comparer);
            _directory = new STMVariable<Directory>(new Directory(new Dictionary<TKey, Entry>(comparer)));
        }

        /// <summary>
        /// Attempts to read the value associated with <paramref name="key"/>. Reads the directory
        /// (to observe a consistent key set) and, when present, the key's value cell.
        /// </summary>
        public bool TryGetValue(ITransaction transaction, TKey key, out TValue value)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(key);

            var directory = transaction.Read(_directory);
            if (directory.Map.TryGetValue(key, out var entry))
            {
                value = transaction.Read(entry.Cell);
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Reads the value associated with <paramref name="key"/>, throwing if it is absent.
        /// </summary>
        public TValue Get(ITransaction transaction, TKey key)
        {
            if (TryGetValue(transaction, key, out var value))
                return value;

            throw new KeyNotFoundException($"The key was not present in the transactional dictionary.");
        }

        /// <summary>
        /// Returns whether <paramref name="key"/> is present. Reads the directory only, so the
        /// observed absence or presence is validated at commit (phantom prevention).
        /// </summary>
        public bool ContainsKey(ITransaction transaction, TKey key)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(key);

            return transaction.Read(_directory).Map.ContainsKey(key);
        }

        /// <summary>
        /// Inserts or updates <paramref name="key"/>. Updating an existing key writes only that
        /// key's value cell (fine-grained, no structural change). Inserting a new key replaces the
        /// directory snapshot (structural change).
        /// </summary>
        public void Set(ITransaction transaction, TKey key, TValue value)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(key);

            var directory = transaction.Read(_directory);
            if (directory.Map.TryGetValue(key, out var entry))
            {
                // Existing key: independent of every other key's value and of the key set.
                transaction.Write(entry.Cell, value);
                return;
            }

            // New key: copy-on-write the directory so the published snapshot stays immutable.
            var updated = new Dictionary<TKey, Entry>(directory.Map)
            {
                [key] = new Entry(value)
            };
            transaction.Write(_directory, new Directory(updated));
        }

        /// <summary>
        /// Removes <paramref name="key"/> if present, returning whether a removal occurred.
        /// A removal is a structural change and replaces the directory snapshot.
        /// </summary>
        public bool Remove(ITransaction transaction, TKey key)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(key);

            var directory = transaction.Read(_directory);
            if (!directory.Map.ContainsKey(key))
                return false;

            var updated = new Dictionary<TKey, Entry>(directory.Map);
            updated.Remove(key);
            transaction.Write(_directory, new Directory(updated));
            return true;
        }

        /// <summary>
        /// Returns the number of keys observed in the current directory snapshot.
        /// </summary>
        public int Count(ITransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);

            return transaction.Read(_directory).Map.Count;
        }
    }
}
