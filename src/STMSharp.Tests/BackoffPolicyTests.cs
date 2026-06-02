// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Backoff;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    public class BackoffPolicyTests
    {
        // -------- Exponential --------

        [Theory]
        [InlineData(0, 100, 100)]   // 100 * 2^0 = 100
        [InlineData(1, 100, 200)]   // 100 * 2^1 = 200
        [InlineData(2, 100, 400)]   // 100 * 2^2 = 400
        [InlineData(3, 100, 800)]   // 100 * 2^3 = 800
        public void Exponential_ReturnsExpectedDelay(int attempt, int baseDelay, int expected)
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Exponential, attempt, baseDelay, maxDelay: 10000);
            Assert.Equal(expected, delay);
        }

        [Fact]
        public void Exponential_IsCappedByMaxDelay()
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Exponential, 20, baseDelay: 100, maxDelay: 500);
            Assert.Equal(500, delay);
        }

        // -------- ExponentialWithJitter --------

        [Fact]
        public void ExponentialWithJitter_ReturnsWithinExpectedRange()
        {
            // For attempt=2, baseDelay=100: CapExp = min(400, maxDelay=10000) = 400.
            // This is full-jitter: Random.Shared.Next(0, CapExp + 1) → range [0, 400]
            // inclusive. Zero is a deliberate, valid outcome; it is what breaks synchronized
            // retry storms, so the test must allow it.
            for (int i = 0; i < 50; i++)
            {
                var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.ExponentialWithJitter, 2, 100, 10000);
                Assert.InRange(delay, 0, 400);
            }
        }

        // -------- Linear --------

        [Theory]
        [InlineData(0, 100, 100)]    // 100 * (0+1) = 100
        [InlineData(1, 100, 200)]    // 100 * (1+1) = 200
        [InlineData(4, 100, 500)]    // 100 * (4+1) = 500
        public void Linear_ReturnsExpectedDelay(int attempt, int baseDelay, int expected)
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Linear, attempt, baseDelay, maxDelay: 10000);
            Assert.Equal(expected, delay);
        }

        [Fact]
        public void Linear_IsCappedByMaxDelay()
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Linear, 100, baseDelay: 100, maxDelay: 500);
            Assert.Equal(500, delay);
        }

        [Fact]
        public void Linear_LargeValues_DoNotOverflow()
        {
            // baseDelay=100_000, attempt=100_000 → would overflow int without the long cast
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Linear, 100_000, baseDelay: 100_000, maxDelay: 2000);
            Assert.Equal(2000, delay);
        }

        // -------- Constant --------

        [Fact]
        public void Constant_AlwaysReturnsBaseDelay()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Constant, attempt, baseDelay: 42, maxDelay: 10000);
                Assert.Equal(42, delay);
            }
        }

        // -------- Edge cases --------

        [Fact]
        public void NegativeAttempt_IsClampedToZero()
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Exponential, -5, baseDelay: 100, maxDelay: 10000);
            // attempt clamped to 0 → 100 * 2^0 = 100
            Assert.Equal(100, delay);
        }

        [Fact]
        public void NegativeBaseDelay_IsClampedToZero()
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Constant, 0, baseDelay: -50, maxDelay: 10000);
            Assert.Equal(0, delay);
        }

        [Fact]
        public void NegativeMaxDelay_IsClampedToZero()
        {
            var delay = BackoffPolicy.GetDelayMilliseconds(BackoffType.Exponential, 10, baseDelay: 100, maxDelay: -1);
            Assert.Equal(0, delay);
        }

        [Fact]
        public void InvalidBackoffType_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BackoffPolicy.GetDelayMilliseconds((BackoffType)999, 1, 100, 2000));
        }

        // -------- GetDelay (TimeSpan overload) --------

        [Fact]
        public void GetDelay_ReturnsTimeSpan_ConsistentWithMilliseconds()
        {
            var ms = BackoffPolicy.GetDelayMilliseconds(BackoffType.Exponential, 3, 100, 10000);
            var ts = BackoffPolicy.GetDelay(BackoffType.Exponential, 3, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10000));

            Assert.Equal(ms, (int)ts.TotalMilliseconds);
        }

        [Fact]
        public void GetDelay_NullMaxDelay_UsesDefaultOf2000()
        {
            var ts = BackoffPolicy.GetDelay(BackoffType.Exponential, 20, TimeSpan.FromMilliseconds(100), maxDelay: null);
            Assert.True(ts.TotalMilliseconds <= 2000);
        }
    }
}
