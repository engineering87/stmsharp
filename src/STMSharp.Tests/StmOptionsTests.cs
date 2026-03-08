// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    public class StmOptionsTests
    {
        [Fact]
        public void Default_HasExpectedValues()
        {
            var d = StmOptions.Default;

            Assert.Equal(3, d.MaxAttempts);
            Assert.Equal(TimeSpan.FromMilliseconds(100), d.BaseDelay);
            Assert.Equal(TimeSpan.FromMilliseconds(2000), d.MaxDelay);
            Assert.Equal(BackoffType.ExponentialWithJitter, d.Strategy);
            Assert.Equal(TransactionMode.ReadWrite, d.Mode);
            Assert.False(d.IsReadOnly);
        }

        [Fact]
        public void ReadOnly_HasReadOnlyMode()
        {
            var ro = StmOptions.ReadOnly;

            Assert.Equal(TransactionMode.ReadOnly, ro.Mode);
            Assert.True(ro.IsReadOnly);
        }

        [Fact]
        public void ReadOnly_InheritsDefaultRetryPolicy()
        {
            var ro = StmOptions.ReadOnly;
            var d = StmOptions.Default;

            Assert.Equal(d.MaxAttempts, ro.MaxAttempts);
            Assert.Equal(d.BaseDelay, ro.BaseDelay);
            Assert.Equal(d.MaxDelay, ro.MaxDelay);
            Assert.Equal(d.Strategy, ro.Strategy);
        }

        [Fact]
        public void IsReadOnly_MatchesMode()
        {
            var rw = new StmOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(10), Mode: TransactionMode.ReadWrite);
            var ro = new StmOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(10), Mode: TransactionMode.ReadOnly);

            Assert.False(rw.IsReadOnly);
            Assert.True(ro.IsReadOnly);
        }

        [Fact]
        public void NullMaxDelay_IsAllowed()
        {
            var opts = new StmOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(10), MaxDelay: null);
            Assert.Null(opts.MaxDelay);
        }

        [Fact]
        public void WithExpression_CreatesModifiedCopy()
        {
            var original = StmOptions.Default;
            var modified = original with { MaxAttempts = 10, Strategy = BackoffType.Linear };

            Assert.Equal(10, modified.MaxAttempts);
            Assert.Equal(BackoffType.Linear, modified.Strategy);
            // Original unchanged
            Assert.Equal(3, original.MaxAttempts);
            Assert.Equal(BackoffType.ExponentialWithJitter, original.Strategy);
        }

        [Fact]
        public void ZeroMaxAttempts_ThrowsArgumentOutOfRangeException()
        {
            // StmOptions validates MaxAttempts > 0 at construction time
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new StmOptions(MaxAttempts: 0, BaseDelay: TimeSpan.FromMilliseconds(1)));
        }

        [Fact]
        public void NegativeBaseDelay_ThrowsArgumentOutOfRangeException()
        {
            // StmOptions validates BaseDelay >= TimeSpan.Zero at construction time
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new StmOptions(MaxAttempts: 1, BaseDelay: TimeSpan.FromMilliseconds(-100)));
        }
    }
}
