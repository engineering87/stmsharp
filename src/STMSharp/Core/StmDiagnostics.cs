// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    /// <summary>
    /// Public diagnostics helper for STM internals (per closed generic type Transaction&lt;T&gt;).
    /// Provides read-only access to conflict/retry counters and a way to reset them.
    /// </summary>
    public static class StmDiagnostics
    {
        /// <summary>
        /// Resets global counters (per closed generic type).
        /// </summary>
        public static void Reset<T>() => Transaction<T>.ResetCounters();

        /// <summary>
        /// Returns the total number of detected conflicts for Transaction&lt;T&gt;.
        /// </summary>
        public static int GetConflictCount<T>() => Transaction<T>.ConflictCount;

        /// <summary>
        /// Returns the total number of retry attempts for Transaction&lt;T&gt;.
        /// </summary>
        public static int GetRetryCount<T>() => Transaction<T>.RetryCount;
    }
}