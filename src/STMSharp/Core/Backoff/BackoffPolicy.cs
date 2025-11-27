// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Enum;

namespace STMSharp.Core.Backoff
{
    /// <summary>
    /// Provides several retry backoff strategies used by the STM engine
    /// when a transactional commit fails (usually because of conflicts).
    ///
    /// The goal of a backoff strategy is to reduce contention and allow
    /// other transactions to make progress, avoiding excessive retries,
    /// wasted CPU cycles, and possible livelocks.
    ///
    /// All methods in this class are deterministic except when jitter
    /// (randomized delay) is explicitly used.
    /// </summary>
    public static class BackoffPolicy
    {
        /// <summary>
        /// Calculates the delay in milliseconds to wait before retrying an operation,
        /// based on the specified backoff algorithm and the number of retry attempts.
        ///
        /// Notes:
        /// - Attempts are zero-based, but always clamped to >= 0.
        /// - baseDelay and maxDelay are also clamped to >= 1.
        /// - Exponential backoff is capped to prevent overflow (max shift = 30).
        /// - ExponentialWithJitter introduces randomness to reduce synchronized retries.
        /// </summary>
        public static int GetDelayMilliseconds(
            BackoffType type,
            int attempt,
            int baseDelay = 100,
            int maxDelay = 2000)
        {
            attempt = Math.Max(0, attempt);
            baseDelay = Math.Max(1, baseDelay);
            maxDelay = Math.Max(1, maxDelay);

            // Local helper for exponential calculation:
            // (baseDelay * 2^attempt) but capped to avoid overflow.
            int CapExp(int a)
            {
                int shift = Math.Min(a, 30);
                long value = ((long)baseDelay) << shift;
                return (int)Math.Clamp(value, 1, maxDelay);
            }

            return type switch
            {
                BackoffType.Exponential =>
                    // Pure exponential backoff with capping
                    CapExp(attempt),

                BackoffType.ExponentialWithJitter =>
                    // Adds randomness in the range [1, CapExp(attempt)] to reduce herd effects
                    Random.Shared.Next(1, CapExp(attempt) + 1),

                BackoffType.Linear =>
                    // Linearly increasing delay: baseDelay * (attempt + 1)
                    Math.Min(baseDelay * (attempt + 1), maxDelay),

                BackoffType.Constant =>
                    // Always return baseDelay
                    baseDelay,

                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }

        /// <summary>
        /// Returns the backoff delay as a TimeSpan instead of milliseconds.
        ///
        /// This overload supports TimeSpan inputs and is internally mapped to
        /// the millisecond-based variant for consistency and correctness.
        ///
        /// Useful when the STM engine needs a proper TimeSpan (e.g. async Task.Delay).
        /// </summary>
        public static TimeSpan GetDelay(
            BackoffType type,
            int attempt,
            TimeSpan baseDelay,
            TimeSpan? maxDelay = null)
        {
            var ms = GetDelayMilliseconds(
                type,
                attempt,
                (int)Math.Max(1, baseDelay.TotalMilliseconds),
                (int)Math.Max(1, (maxDelay ?? TimeSpan.FromMilliseconds(2000)).TotalMilliseconds));

            return TimeSpan.FromMilliseconds(ms);
        }
    }
}