// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Interfaces;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// Verifies the exception-free TryAtomic surface: it commits like Atomic on the success
    /// path, and reports budget exhaustion through its return value instead of throwing
    /// TransactionConflictException. This is the path callers use on the contended hot path,
    /// where throwing and unwinding would be expensive.
    /// </summary>
    [Collection("STM non-parallel")]
    public class TryAtomicTests
    {
        [Fact]
        public async Task TryAtomic_Void_CommitsAndReturnsTrue()
        {
            var v = new STMVariable<int>(0);

            bool committed = await STMEngine.TryAtomic(tx =>
            {
                tx.Write(v, tx.Read(v) + 1);
            }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(committed);
            Assert.Equal(1, v.Read());
        }

        [Fact]
        public async Task TryAtomic_WithResult_ReturnsCommittedTrueAndValue()
        {
            var v = new STMVariable<int>(41);

            var (committed, value) = await STMEngine.TryAtomic(async tx =>
            {
                await Task.Yield();
                return tx.Read(v) + 1;
            }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(committed);
            Assert.Equal(42, value);
        }

        [Fact]
        public async Task TryAtomic_BudgetExhausted_ReturnsFalse_DoesNotThrow()
        {
            // Force guaranteed, unavoidable conflict: between this transaction's read and its
            // commit, a competing direct write always advances the version, so every attempt
            // fails read-set validation. With maxAttempts = 1 the budget is exhausted on the
            // first failure. TryAtomic must report false rather than throw.
            var v = new STMVariable<int>(0);

            bool committed = await STMEngine.TryAtomic(tx =>
            {
                // Read v into the read set.
                int seen = tx.Read(v);
                // A concurrent committed write lands before our commit, invalidating the read.
                v.Write(seen + 1000);
                // Now buffer a write so this is a read-write transaction that must validate.
                tx.Write(v, seen + 1);
            },
            maxAttempts: 1,
            initialBackoffMilliseconds: 0,
            maxBackoffMilliseconds: 0,
            backoffType: BackoffType.Constant,
            cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(committed);
        }

        [Fact]
        public async Task TryAtomic_WithResult_BudgetExhausted_ReturnsFalseAndDefault()
        {
            var v = new STMVariable<int>(0);

            var (committed, value) = await STMEngine.TryAtomic(tx =>
            {
                int seen = tx.Read(v);
                v.Write(seen + 1000);   // invalidate the read before commit
                tx.Write(v, seen + 1);
                return Task.FromResult(seen + 1);
            },
            maxAttempts: 1,
            initialBackoffMilliseconds: 0,
            maxBackoffMilliseconds: 0,
            backoffType: BackoffType.Constant,
            cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(committed);
            Assert.Equal(0, value); // default on a non-committed result
        }

        [Fact]
        public async Task TryAtomic_NullArguments_Throw()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await STMEngine.TryAtomic((Action<ITransaction>)null!, cancellationToken: TestContext.Current.CancellationToken);
            });
        }
    }
}
