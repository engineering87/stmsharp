// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    /// <summary>
    /// Process-wide monotonic version clock used by the TL2-style commit protocol.
    ///
    /// Every committing read-write transaction increments this clock once to obtain
    /// a write version, and stamps each written variable with that value. Read-only
    /// snapshots sample the clock at their start. Because the clock only ever moves
    /// forward, a variable whose version exceeds a transaction's start version must
    /// have been written by a concurrent commit, which is exactly the condition the
    /// protocol uses to detect conflicts and to guarantee a consistent snapshot
    /// (opacity) throughout a transaction's execution.
    /// </summary>
    internal static class GlobalVersionClock
    {
        private static long _clock;

        /// <summary>
        /// Reads the current clock value (the latest committed version).
        /// </summary>
        public static long Read() => Volatile.Read(ref _clock);

        /// <summary>
        /// Atomically advances the clock and returns the new write version.
        /// </summary>
        public static long Next() => Interlocked.Increment(ref _clock);
    }
}
