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
        public static StmOptions Default => new(
            MaxAttempts: 3,
            BaseDelay: TimeSpan.FromMilliseconds(100),
            MaxDelay: TimeSpan.FromMilliseconds(2000),
            Strategy: BackoffType.ExponentialWithJitter,
            Mode: TransactionMode.ReadWrite
        );

        public static StmOptions ReadOnly => Default with { Mode = TransactionMode.ReadOnly };

        public bool IsReadOnly => Mode == TransactionMode.ReadOnly;

        internal (int maxAttempts, int baseMs, int maxMs, BackoffType strategy, bool isReadOnly) ToPolicyArgs()
        {
            var baseMs = (int)Math.Max(1, BaseDelay.TotalMilliseconds);
            var maxMs = (int)Math.Max(1, (MaxDelay ?? TimeSpan.FromMilliseconds(2000)).TotalMilliseconds);
            return (Math.Max(1, MaxAttempts), baseMs, maxMs, Strategy, IsReadOnly);
        }
    }
}