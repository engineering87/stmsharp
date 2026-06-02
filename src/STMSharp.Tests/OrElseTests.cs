// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;

namespace STMSharp.Tests
{
    /// <summary>
    /// Verifies ITransaction.OrElse composition: the first alternative runs; if it blocks via
    /// Retry the second runs in its place with the first's tentative writes discarded; and if
    /// both block, the transaction blocks on the union of the variables read by both, so a
    /// change to any of them wakes it.
    /// </summary>
    [Collection("STM non-parallel")]
    public class OrElseTests
    {
        [Fact]
        public async Task OrElse_FirstSucceeds_SecondNotRun()
        {
            var v = new STMVariable<int>(7);
            bool secondRan = false;
            int observed = 0;

            await STMEngine.Atomic(tx =>
            {
                tx.OrElse(
                    first: t => { observed = t.Read(v); },          // completes, does not block
                    second: t => { secondRan = true; });
            });

            Assert.Equal(7, observed);
            Assert.False(secondRan, "Second alternative must not run when the first completes.");
        }

        [Fact]
        public async Task OrElse_FirstBlocks_SecondRuns_FirstWritesDiscarded()
        {
            var gate = new STMVariable<int>(0);   // 0 makes the first alternative block
            var target = new STMVariable<int>(0);

            await STMEngine.Atomic(tx =>
            {
                tx.OrElse(
                    first: t =>
                    {
                        t.Write(target, 999);     // tentative write that must be discarded
                        if (t.Read(gate) == 0)
                            t.Retry();             // blocks: hands over to the second alternative
                    },
                    second: t =>
                    {
                        // Must observe the committed value, not the first alternative's 999.
                        t.Write(target, t.Read(target) + 1);
                    });
            });

            // Second alternative ran on the original value (0 -> 1); first's 999 was rolled back.
            Assert.Equal(1, await STMEngine.Atomic(async t => { await Task.Yield(); return t.Read(target); }));
        }

        [Fact]
        public async Task OrElse_BothBlock_WakesOnUnionOfReadSets()
        {
            // Neither alternative can proceed initially: first watches a, second watches b.
            // The whole orElse must block on {a, b}; committing a change to b must wake it.
            var a = new STMVariable<int>(0);
            var b = new STMVariable<int>(0);

            var waiter = Task.Run(async () =>
            {
                int result = 0;
                await STMEngine.Atomic(tx =>
                {
                    tx.OrElse(
                        first: t => { if (t.Read(a) == 0) t.Retry(); result = 1; },
                        second: t => { if (t.Read(b) == 0) t.Retry(); result = 2; });
                });
                return result;
            });

            Assert.False(waiter.IsCompleted, "Should block while both a and b are zero.");
            await Task.Delay(100);
            Assert.False(waiter.IsCompleted, "Should still block before any commit.");

            // Wake via the SECOND alternative's watched variable, proving union blocking.
            await STMEngine.Atomic(tx => tx.Write(b, 5));

            var finished = await Task.WhenAny(waiter, Task.Delay(5000));
            Assert.Same(waiter, finished);
            Assert.Equal(2, await waiter); // second alternative completed after b changed
        }

        [Fact]
        public async Task OrElse_NullArguments_Throw()
        {
            var v = new STMVariable<int>(0);

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await STMEngine.Atomic(tx => tx.OrElse(null!, t => { }));
            });

            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await STMEngine.Atomic(tx => tx.OrElse(t => { }, null!));
            });
        }
    }
}
