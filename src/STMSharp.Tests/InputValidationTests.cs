// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Core.Interfaces;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    [Collection("STM non-parallel")]
    public class InputValidationTests
    {
        [Fact]
        public async Task Atomic_NullAction_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(
                    (Action<ITransaction<int>>)null!));
        }

        [Fact]
        public async Task Atomic_NullFunc_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(
                    (Func<ITransaction<int>, Task>)null!));
        }

        [Fact]
        public async Task Atomic_NullActionWithOptions_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(
                    (Action<ITransaction<int>>)null!,
                    StmOptions.Default));
        }

        [Fact]
        public async Task Atomic_NullFuncWithOptions_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(
                    (Func<ITransaction<int>, Task>)null!,
                    StmOptions.Default));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-100)]
        public async Task Atomic_MaxAttemptsZeroOrNegative_ThrowsArgumentOutOfRangeException(int maxAttempts)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                STMEngine.Atomic<int>(
                    tx => { },
                    maxAttempts: maxAttempts));
        }

        [Fact]
        public async Task Atomic_NullVariable_Read_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(tx =>
                {
                    tx.Read(null!);
                }));
        }

        [Fact]
        public async Task Atomic_NullVariable_Write_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                STMEngine.Atomic<int>(tx =>
                {
                    tx.Write(null!, 42);
                }));
        }
    }
}
