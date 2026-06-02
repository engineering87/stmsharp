// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// Verifies commutative updates: an operation buffered via ITransaction.Commute is applied
    /// to the live committed value under lock at commit time, so two commuting updates to the
    /// same variable do not conflict. The invariant tests are the important ones: if commute
    /// ever loses or double-applies an update, the final total is wrong and the test fails.
    /// </summary>
    [Collection("STM non-parallel")]
    public class CommuteTests
    {
        [Fact]
        public async Task Commute_SingleTransaction_AppliesOperation()
        {
            var counter = new STMVariable<int>(10);

            await STMEngine.Atomic(tx => tx.Commute(counter, x => x + 5));

            Assert.Equal(15, counter.Read());
        }

        [Fact]
        public async Task Commute_ComposesMultipleOperationsOnSameVariable()
        {
            var counter = new STMVariable<int>(0);

            await STMEngine.Atomic(tx =>
            {
                tx.Commute(counter, x => x + 1);
                tx.Commute(counter, x => x + 10);
                tx.Commute(counter, x => x * 2); // (((0+1)+10)*2) = 22
            });

            Assert.Equal(22, counter.Read());
        }

        [Fact]
        public async Task Commute_ConcurrentIncrements_ConserveTotal()
        {
            // The key invariant: N threads each issuing M commuting increments must produce
            // exactly N*M, with no lost updates, even though every increment targets the
            // same variable. With a plain Read+Write this workload would conflict constantly;
            // with Commute the increments compose under lock at commit.
            var counter = new STMVariable<int>(0);
            const int threads = 16;
            const int perThread = 1000;

            var workers = new Task[threads];
            for (int t = 0; t < threads; t++)
            {
                workers[t] = Task.Run(async () =>
                {
                    for (int i = 0; i < perThread; i++)
                    {
                        await STMEngine.Atomic(
                            tx => tx.Commute(counter, x => x + 1),
                            maxAttempts: 64,
                            initialBackoffMilliseconds: 0,
                            maxBackoffMilliseconds: 2,
                            backoffType: BackoffType.ExponentialWithJitter);
                    }
                });
            }

            await Task.WhenAll(workers);
            Assert.Equal(threads * perThread, counter.Read());
        }

        [Fact]
        public async Task Commute_ThenRead_FallsBackToConservativePath()
        {
            // Reading the same variable after a Commute must observe the operation's effect,
            // which forces the conservative (eager, validated) path. The final value must be
            // consistent with a single applied increment.
            var v = new STMVariable<int>(100);
            int seen = 0;

            await STMEngine.Atomic(tx =>
            {
                tx.Commute(v, x => x + 1);
                seen = tx.Read(v); // forces fallback: must see 101 within the transaction
            });

            Assert.Equal(101, seen);
            Assert.Equal(101, v.Read());
        }

        [Fact]
        public async Task Commute_ThenWrite_WriteWins()
        {
            // An explicit Write after a Commute supersedes the pending commute: the final
            // value is the written value, not the commuted one. This exercises the
            // disjointness fix (the commute entry must be dropped when a write follows).
            var v = new STMVariable<int>(50);

            await STMEngine.Atomic(tx =>
            {
                tx.Commute(v, x => x + 999);
                tx.Write(v, 7);
            });

            Assert.Equal(7, v.Read());
        }

        [Fact]
        public async Task Commute_NullArguments_Throw()
        {
            var v = new STMVariable<int>(0);

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await STMEngine.Atomic(tx => tx.Commute<int>(null!, x => x + 1));
            });

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await STMEngine.Atomic(tx => tx.Commute(v, null!));
            });
        }
    }
}
