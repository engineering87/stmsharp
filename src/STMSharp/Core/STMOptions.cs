// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Enum;

namespace STMSharp.Core
{
    /// <summary>
    /// Represents the configuration options for STM (Software Transactional Memory) operations.
    /// Provides control over retry behavior, backoff strategy, delay intervals, and transaction mode.
    /// This class is immutable and can be reused across multiple transactions.
    /// </summary>
    public sealed record StmOptions(
        int MaxAttempts,
        TimeSpan BaseDelay,
        TimeSpan? MaxDelay = null,
        BackoffType Strategy = BackoffType.ExponentialWithJitter,
        TransactionMode Mode = TransactionMode.ReadWrite)
    {
        public int MaxAttempts { get; init; } = MaxAttempts > 0
            ? MaxAttempts
            : throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be greater than 0.");

        public TimeSpan BaseDelay { get; init; } = BaseDelay >= TimeSpan.Zero
            ? BaseDelay
            : throw new ArgumentOutOfRangeException(nameof(BaseDelay), "BaseDelay must be non-negative.");

        private static readonly StmOptions s_default = new(
            MaxAttempts: 3,
            BaseDelay: TimeSpan.FromMilliseconds(100),
            MaxDelay: TimeSpan.FromMilliseconds(2000),
            Strategy: BackoffType.ExponentialWithJitter,
            Mode: TransactionMode.ReadWrite
        );

        private static readonly StmOptions s_readOnly = s_default with { Mode = TransactionMode.ReadOnly };

        public static StmOptions Default => s_default;

        public static StmOptions ReadOnly => s_readOnly;

        public bool IsReadOnly => Mode == TransactionMode.ReadOnly;

        internal (int maxAttempts, int baseMs, int maxMs, BackoffType strategy, bool isReadOnly) ToPolicyArgs()
        {
            var baseMs = (int)Math.Clamp(BaseDelay.TotalMilliseconds, 0, int.MaxValue);
            var maxMs = (int)Math.Clamp((MaxDelay ?? TimeSpan.FromMilliseconds(2000)).TotalMilliseconds, 0, int.MaxValue);

            return (Math.Max(1, MaxAttempts), baseMs, maxMs, Strategy, IsReadOnly);
        }
    }
}