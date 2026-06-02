// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Runtime.CompilerServices;

namespace STMSharp.Core
{
    /// <summary>
    /// Encoding of a versioned write-lock into a single 64-bit word.
    ///
    /// Bit 0 is the lock flag and the remaining bits hold the version:
    /// <list type="bullet">
    /// <item><description>even word (bit 0 clear) =&gt; unlocked, version = word >> 1</description></item>
    /// <item><description>odd word  (bit 0 set)   =&gt; locked by a committer</description></item>
    /// </list>
    /// Storing the lock flag and the version in one word lets a single atomic read
    /// observe both, which the read validation relies on for a consistent snapshot.
    /// </summary>
    internal static class VersionLock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLocked(long word) => (word & 1L) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long VersionOf(long word) => word >> 1;

        /// <summary>
        /// Builds the unlocked word that represents the given version.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Stamp(long version) => version << 1;
    }
}
