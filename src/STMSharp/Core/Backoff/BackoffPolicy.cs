// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Enum;

namespace STMSharp.Core.Backoff
{
    public static class BackoffPolicy
    {
        /// <summary>
        /// Calculates the delay in milliseconds to wait before retrying an operation,
        /// based on the specified backoff algorithm and attempt count.
        /// </summary>
        /// <param name="type">The backoff strategy to apply (e.g., Exponential, ExponentialWithJitter, Linear, Constant).</param>
        /// <param name="attempt">The current retry attempt count (zero-based).</param>
        /// <param name="baseDelay">The base delay in milliseconds used for calculations (default is 100ms).</param>
        /// <param name="maxDelay">The maximum allowed delay in milliseconds (default is 2000ms).</param>
        /// <returns>The delay in milliseconds to wait before the next retry.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if an unsupported backoff type is specified.</exception>
        public static int GetDelayMilliseconds(
            BackoffType type,
            int attempt,
            int baseDelay = 100,
            int maxDelay = 2000)
        {
            attempt = Math.Max(0, attempt);
            baseDelay = Math.Max(1, baseDelay);
            maxDelay = Math.Max(1, maxDelay);

            int CapExp(int a)
            {
                int shift = Math.Min(a, 30); // prevent overflow
                long value = ((long)baseDelay) << shift;
                return (int)Math.Clamp(value, 1, maxDelay);
            }

            return type switch
            {
                BackoffType.Exponential => CapExp(attempt),
                BackoffType.ExponentialWithJitter => Random.Shared.Next(0, CapExp(attempt) + 1),
                BackoffType.Linear => Math.Min(baseDelay * (attempt + 1), maxDelay),
                BackoffType.Constant => baseDelay,
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }
    }
}
