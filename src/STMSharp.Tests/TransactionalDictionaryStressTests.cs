// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Collections;

namespace STMSharp.Tests
{
    /// <summary>
    /// Multi-threaded stress tests for the fine-grained transactional dictionary, exercising the
    /// interaction of the three operation paths under contention: fine-grained value updates,
    /// structural insertions, and structural removals, all on the same shared instance.
    ///
    /// These tests target the boundary that the functional suite cannot reach. A defect in the
    /// composition of the value path with the structural path, or a failure of the opacity
    /// validation that prevents phantom reads, does not surface in single-threaded semantics; it
    /// surfaces only when many transactions observe and mutate the directory snapshot concurrently.
    /// Each test asserts an invariant that holds for every legal interleaving and is violated by a
    /// non-atomic or non-opaque commit, so a failure is a correctness signal, not a timing artifact.
    ///
    /// Every operation is a single all-or-nothing transaction, so a transaction that aborts and is
    /// retried, or that is ultimately abandoned, leaves the asserted invariant intact; only a
    /// committed atomicity violation can break it. The per-call attempt budget is generous so that
    /// contention on the single structural variable is absorbed by the engine's own retry loop.
    /// </summary>
    [Collection("STM non-parallel")]
    public class TransactionalDictionaryStressTests
    {
        // Generous so that contention on the single directory variable is resolved by the engine's
        // internal retry loop rather than by exhausting the budget during the measured window.
        private const int MaxAttempts = 1000;

        /// <summary>
        /// Money-conservation invariant across a dynamic key set. A fixed, never-removed vault key
        /// holds the initial money supply; a bounded universe of account keys is opened, closed, and
        /// transferred between by concurrent workers. Every transaction is value-preserving by
        /// construction: opening an account moves money out of the vault, closing one moves it back,
        /// and a transfer moves money between two accounts. Because the key universe is fixed and
        /// known, the total can be recomputed at the end without enumerating the live key set.
        ///
        /// The asserted invariant is that the vault balance plus the sum of all present account
        /// balances equals the original supply exactly. This is violated if a structural mutation and
        /// the value write it is paired with fail to commit atomically, if a phantom lets two workers
        /// both open the same absent account from the same vault snapshot, or if any read escapes
        /// opacity validation and a stale balance is written back.
        /// </summary>
        [Fact]
        public async Task ConcurrentOpensClosesAndTransfers_PreserveMoneyConservation()
        {
            const int Accounts = 8;
            const int Workers = 6;
            const int OpsPerWorker = 120;
            const int VaultKey = -1;
            const int TotalMoney = 1_000_000;

            var dict = new TransactionalDictionary<int, int>();
            await STMEngine.Atomic(tx => dict.Set(tx, VaultKey, TotalMoney));

            async Task Worker(int seed)
            {
                var rng = new Random(seed);
                for (int op = 0; op < OpsPerWorker; op++)
                {
                    // Choices are fixed before the transaction so that a retry replays the same
                    // intent against fresh state rather than drifting to a different operation.
                    int action = rng.Next(3);
                    int accountA = rng.Next(Accounts);
                    int accountB = rng.Next(Accounts);
                    int amount = rng.Next(1, 101);

                    await STMEngine.Atomic(tx =>
                    {
                        switch (action)
                        {
                            case 0: // open: move money from the vault into a newly created account
                            {
                                if (!dict.ContainsKey(tx, accountA))
                                {
                                    int vault = dict.Get(tx, VaultKey);
                                    if (vault >= amount)
                                    {
                                        dict.Set(tx, VaultKey, vault - amount);
                                        dict.Set(tx, accountA, amount);
                                    }
                                }
                                break;
                            }
                            case 1: // close: move the account balance back into the vault, then remove
                            {
                                if (dict.TryGetValue(tx, accountA, out int balance))
                                {
                                    int vault = dict.Get(tx, VaultKey);
                                    dict.Set(tx, VaultKey, vault + balance);
                                    dict.Remove(tx, accountA);
                                }
                                break;
                            }
                            default: // transfer: move money between two existing accounts
                            {
                                if (accountA != accountB
                                    && dict.TryGetValue(tx, accountA, out int sourceBalance)
                                    && dict.TryGetValue(tx, accountB, out int targetBalance)
                                    && sourceBalance > 0)
                                {
                                    int moved = Math.Min(amount, sourceBalance);
                                    dict.Set(tx, accountA, sourceBalance - moved);
                                    dict.Set(tx, accountB, targetBalance + moved);
                                }
                                break;
                            }
                        }
                    },
                    maxAttempts: MaxAttempts);
                }
            }

            var workers = new List<Task>();
            for (int w = 0; w < Workers; w++)
            {
                int seed = 1000 + w;
                workers.Add(Task.Run(() => Worker(seed)));
            }
            await Task.WhenAll(workers);

            long total = 0;
            int vaultBalance = 0;
            bool allNonNegative = true;
            await STMEngine.Atomic(tx =>
            {
                vaultBalance = dict.Get(tx, VaultKey);
                long sum = vaultBalance;
                for (int i = 0; i < Accounts; i++)
                {
                    if (dict.TryGetValue(tx, i, out int balance))
                    {
                        if (balance < 0)
                            allNonNegative = false;
                        sum += balance;
                    }
                }
                total = sum;
            });

            Assert.True(vaultBalance >= 0, "the vault balance must never go negative");
            Assert.True(allNonNegative, "no account balance may go negative");
            Assert.Equal((long)TotalMoney, total);
        }

        /// <summary>
        /// Structural lost-update invariant for insertion. A set of distinct keys is inserted
        /// concurrently, each in its own transaction, by several workers contending on the single
        /// directory variable. Because every insertion copies the current snapshot and publishes a
        /// new one, a transaction that built its new snapshot from a stale directory and committed
        /// without revalidation would silently erase a concurrently inserted key.
        ///
        /// The asserted invariant is that every distinct key survives: the final count equals the
        /// number of distinct keys, and each key is present with its expected value. A shortfall in
        /// the count is a lost structural update, which is exactly the failure that opacity
        /// validation on the directory variable is meant to prevent.
        /// </summary>
        [Fact]
        public async Task ConcurrentInsertsOfDistinctKeys_AllSurvive()
        {
            const int Keys = 48;
            const int Workers = 6;

            var dict = new TransactionalDictionary<int, int>();

            var workers = new List<Task>();
            for (int w = 0; w < Workers; w++)
            {
                int taskId = w;
                workers.Add(Task.Run(async () =>
                {
                    for (int k = taskId; k < Keys; k += Workers)
                    {
                        int key = k;
                        await STMEngine.Atomic(tx => dict.Set(tx, key, key * 7), maxAttempts: MaxAttempts);
                    }
                }));
            }
            await Task.WhenAll(workers);

            int count = -1;
            await STMEngine.Atomic(tx => count = dict.Count(tx));
            Assert.Equal(Keys, count);

            bool allPresent = true;
            bool allValuesIntact = true;
            await STMEngine.Atomic(tx =>
            {
                for (int k = 0; k < Keys; k++)
                {
                    if (!dict.TryGetValue(tx, k, out int value))
                        allPresent = false;
                    else if (value != k * 7)
                        allValuesIntact = false;
                }
            });

            Assert.True(allPresent, "every concurrently inserted key must be present");
            Assert.True(allValuesIntact, "every concurrently inserted value must be intact");
        }

        /// <summary>
        /// Insert-and-remove churn against a deterministic expected key set. One half of the key
        /// universe is pre-seeded and removed concurrently while the other half is inserted
        /// concurrently, so insertions and removals contend on the same directory variable at the
        /// same time. The final key set is fully determined regardless of interleaving.
        ///
        /// The asserted invariant is that, at the end, every removed key is absent, every inserted
        /// key is present with its expected value, and the count equals the size of one half. A
        /// removal that erased a concurrent insertion, or an insertion that resurrected a concurrent
        /// removal, would break the expected set, which only a non-opaque structural commit can do.
        /// </summary>
        [Fact]
        public async Task ConcurrentInsertsAndRemoves_FinalSetIsExact()
        {
            const int Half = 24;          // keys [0, Half) are seeded then removed
            const int Workers = 3;        // workers per side

            var dict = new TransactionalDictionary<int, int>();
            await STMEngine.Atomic(tx =>
            {
                for (int i = 0; i < Half; i++)
                    dict.Set(tx, i, i);
            });

            var workers = new List<Task>();

            // Removers: delete the seeded keys [0, Half).
            for (int w = 0; w < Workers; w++)
            {
                int taskId = w;
                workers.Add(Task.Run(async () =>
                {
                    for (int k = taskId; k < Half; k += Workers)
                    {
                        int key = k;
                        await STMEngine.Atomic(tx => dict.Remove(tx, key), maxAttempts: MaxAttempts);
                    }
                }));
            }

            // Inserters: add the fresh keys [Half, 2 * Half).
            for (int w = 0; w < Workers; w++)
            {
                int taskId = w;
                workers.Add(Task.Run(async () =>
                {
                    for (int k = Half + taskId; k < 2 * Half; k += Workers)
                    {
                        int key = k;
                        await STMEngine.Atomic(tx => dict.Set(tx, key, key * 11), maxAttempts: MaxAttempts);
                    }
                }));
            }

            await Task.WhenAll(workers);

            bool removedAbsent = true;
            bool insertedPresent = true;
            bool insertedValuesIntact = true;
            int count = -1;
            await STMEngine.Atomic(tx =>
            {
                for (int k = 0; k < Half; k++)
                {
                    if (dict.ContainsKey(tx, k))
                        removedAbsent = false;
                }
                for (int k = Half; k < 2 * Half; k++)
                {
                    if (!dict.TryGetValue(tx, k, out int value))
                        insertedPresent = false;
                    else if (value != k * 11)
                        insertedValuesIntact = false;
                }
                count = dict.Count(tx);
            });

            Assert.True(removedAbsent, "every concurrently removed key must be absent");
            Assert.True(insertedPresent, "every concurrently inserted key must be present");
            Assert.True(insertedValuesIntact, "every concurrently inserted value must be intact");
            Assert.Equal(Half, count);
        }
    }
}
