// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Collections;

namespace STMSharp.Tests
{
    /// <summary>
    /// Functional and concurrency tests for the fine-grained transactional dictionary.
    /// Concurrency correctness is only fully exercised by the stress suite; these cover
    /// transactional semantics and a fine-grained value-update smoke test.
    /// </summary>
    [Collection("STM non-parallel")]
    public class TransactionalDictionaryTests
    {
        [Fact]
        public async Task Set_ThenGet_ReturnsValue()
        {
            var dict = new TransactionalDictionary<string, int>();

            await STMEngine.Atomic(tx => dict.Set(tx, "a", 42));

            int observed = -1;
            await STMEngine.Atomic(tx => observed = dict.Get(tx, "a"));

            Assert.Equal(42, observed);
        }

        [Fact]
        public async Task Set_ExistingKey_UpdatesValue()
        {
            var dict = new TransactionalDictionary<string, int>();
            await STMEngine.Atomic(tx => dict.Set(tx, "a", 1));

            await STMEngine.Atomic(tx => dict.Set(tx, "a", 2));

            int observed = -1;
            await STMEngine.Atomic(tx => observed = dict.Get(tx, "a"));
            Assert.Equal(2, observed);

            int count = -1;
            await STMEngine.Atomic(tx => count = dict.Count(tx));
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task TryGetValue_AbsentKey_ReturnsFalse()
        {
            var dict = new TransactionalDictionary<string, int>();

            bool found = true;
            await STMEngine.Atomic(tx => found = dict.TryGetValue(tx, "missing", out _));

            Assert.False(found);
        }

        [Fact]
        public async Task Remove_Existing_ReturnsTrueAndKeyGone()
        {
            var dict = new TransactionalDictionary<string, int>();
            await STMEngine.Atomic(tx => dict.Set(tx, "a", 1));

            bool removed = false;
            await STMEngine.Atomic(tx => removed = dict.Remove(tx, "a"));
            Assert.True(removed);

            bool present = true;
            await STMEngine.Atomic(tx => present = dict.ContainsKey(tx, "a"));
            Assert.False(present);
        }

        [Fact]
        public async Task Remove_Absent_ReturnsFalse()
        {
            var dict = new TransactionalDictionary<string, int>();

            bool removed = true;
            await STMEngine.Atomic(tx => removed = dict.Remove(tx, "missing"));

            Assert.False(removed);
        }

        [Fact]
        public async Task MultipleOperations_InOneTransaction_CommitAtomically()
        {
            var dict = new TransactionalDictionary<string, int>();

            await STMEngine.Atomic(tx =>
            {
                dict.Set(tx, "a", 1);
                dict.Set(tx, "b", 2);
                dict.Set(tx, "a", 10);
            });

            int a = -1, b = -1, count = -1;
            await STMEngine.Atomic(tx =>
            {
                a = dict.Get(tx, "a");
                b = dict.Get(tx, "b");
                count = dict.Count(tx);
            });

            Assert.Equal(10, a);
            Assert.Equal(2, b);
            Assert.Equal(2, count);
        }

        [Fact]
        public async Task ConcurrentUpdates_OnDistinctKeys_Converge()
        {
            // Exercises the fine-grained path: writers on different existing keys
            // should not conflict, and every increment must be accounted for.
            var dict = new TransactionalDictionary<string, int>();
            await STMEngine.Atomic(tx =>
            {
                dict.Set(tx, "a", 0);
                dict.Set(tx, "b", 0);
            });

            const int tasksPerKey = 4;
            const int incrementsPerTask = 50;

            async Task IncrementLoop(string key)
            {
                for (int i = 0; i < incrementsPerTask; i++)
                {
                    await STMEngine.Atomic(tx =>
                    {
                        var current = dict.Get(tx, key);
                        dict.Set(tx, key, current + 1);
                    },
                    maxAttempts: 64);
                }
            }

            var tasks = new List<Task>();
            for (int i = 0; i < tasksPerKey; i++)
            {
                tasks.Add(Task.Run(() => IncrementLoop("a")));
                tasks.Add(Task.Run(() => IncrementLoop("b")));
            }
            await Task.WhenAll(tasks);

            int a = -1, b = -1;
            await STMEngine.Atomic(tx =>
            {
                a = dict.Get(tx, "a");
                b = dict.Get(tx, "b");
            });

            Assert.Equal(tasksPerKey * incrementsPerTask, a);
            Assert.Equal(tasksPerKey * incrementsPerTask, b);
        }
    }
}
