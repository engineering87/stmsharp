// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Enum;

namespace STMSharp.Core.Backoff
{
    public static class BackoffPolicy
    {
        private static readonly ThreadLocal<Random> _random = new(() => new Random());

        private static int NextRandom(int max)
        {
            return _random.Value!.Next(0, max);
        }

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
            if (baseDelay <= 0) baseDelay = 1;
            if (maxDelay <= 0) maxDelay = 1;

            int CapExp(int a)
            {
                int shift = Math.Min(a, 30); // overflow protection
                long v = ((long)baseDelay) << shift;
                v = Math.Min(v, (long)maxDelay);
                return (int)Math.Max(1, v);
            }

            return type switch
            {
                BackoffType.Exponential => CapExp(attempt),
                BackoffType.ExponentialWithJitter => Random.Shared.Next(0, CapExp(attempt) + 1),
                BackoffType.Linear => Math.Min(Math.Max(1, baseDelay * (attempt + 1)), maxDelay),
                BackoffType.Constant => Math.Max(1, baseDelay),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }
    }
}
