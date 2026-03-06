// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    [Collection("STM non-parallel")]
    public class TransactionSemanticsTests
    {
        private static void ResetStats() => STMDiagnostics.Reset<int>();

        // -------- Read-after-write returns pending value --------

        [Fact]
        public async Task ReadAfterWrite_ReturnsPendingValue()
        {
            ResetStats();
            var x = new STMVariable<int>(10);

            await STMEngine.Atomic<int>(tx =>
            {
                tx.Write(x, 99);
                var v = tx.Read(x);
                Assert.Equal(99, v);
            });
        }

        [Fact]
        public async Task MultipleReads_ReturnCachedSnapshotValue()
        {
            ResetStats();
            var x = new STMVariable<int>(42);

            await STMEngine.Atomic<int>(tx =>
            {
                var v1 = tx.Read(x);
                var v2 = tx.Read(x);
                Assert.Equal(v1, v2);
                Assert.Equal(42, v1);
            });
        }

        // -------- User exception propagation --------

        [Fact]
        public async Task UserExceptionInAction_PropagatesUnwrapped()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                STMEngine.Atomic<int>(tx =>
                {
                    tx.Read(x);
                    throw new InvalidOperationException("user error");
                }));

            Assert.Equal("user error", ex.Message);
        }

        [Fact]
        public async Task UserExceptionInAsyncFunc_PropagatesUnwrapped()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                STMEngine.Atomic<int>(async tx =>
                {
                    tx.Read(x);
                    await Task.Yield();
                    throw new InvalidOperationException("async user error");
                }));

            Assert.Equal("async user error", ex.Message);
        }

        // -------- Async overload --------

        [Fact]
        public async Task AsyncOverload_CommitsCorrectly()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await STMEngine.Atomic<int>(async tx =>
            {
                var v = tx.Read(x);
                await Task.Yield();
                tx.Write(x, v + 5);
            });

            int result = 0;
            await STMEngine.Atomic<int>(tx => { result = tx.Read(x); });
            Assert.Equal(5, result);
        }

        // -------- Write-only (no prior read) --------

        [Fact]
        public async Task WriteOnly_MultipleVariables_CommitsAll()
        {
            ResetStats();
            var a = new STMVariable<int>(0);
            var b = new STMVariable<int>(0);

            await STMEngine.Atomic<int>(tx =>
            {
                tx.Write(a, 100);
                tx.Write(b, 200);
            });

            int ra = 0, rb = 0;
            await STMEngine.Atomic<int>(tx =>
            {
                ra = tx.Read(a);
                rb = tx.Read(b);
            });

            Assert.Equal(100, ra);
            Assert.Equal(200, rb);
        }

        // -------- Overwrite within same transaction --------

        [Fact]
        public async Task MultipleWritesSameVariable_LastWriteWins()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await STMEngine.Atomic<int>(tx =>
            {
                tx.Write(x, 10);
                tx.Write(x, 20);
                tx.Write(x, 30);
            });

            int result = 0;
            await STMEngine.Atomic<int>(tx => { result = tx.Read(x); });
            Assert.Equal(30, result);
        }

        // -------- Read-only transaction sees consistent snapshot --------

        [Fact]
        public async Task ReadOnlyTransaction_SeesConsistentSnapshot()
        {
            ResetStats();
            var a = new STMVariable<int>(10);
            var b = new STMVariable<int>(20);

            int ra = 0, rb = 0;
            await STMEngine.Atomic<int>(tx =>
            {
                ra = tx.Read(a);
                rb = tx.Read(b);
            },
            readOnly: true);

            Assert.Equal(10, ra);
            Assert.Equal(20, rb);
        }
    }
}
