// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    /// <summary>
    /// Public diagnostics helper exposing process-wide conflict, retry, and
    /// unresolved-conflict counters for the STM engine.
    ///
    /// Counters are global to the engine. The generic overloads are retained for
    /// source compatibility and ignore their type argument; prefer the non-generic
    /// methods.
    /// </summary>
    public static class STMDiagnostics
    {
        /// <summary>Resets all global counters.</summary>
        public static void Reset() => StmTransaction.ResetCounters();

        /// <summary>Total number of detected conflicts.</summary>
        public static int GetConflictCount() => StmTransaction.ConflictCount;

        /// <summary>Total number of retry attempts.</summary>
        public static int GetRetryCount() => StmTransaction.RetryCount;

        /// <summary>Total number of transactions that exhausted their retry budget.</summary>
        public static int GetUnresolvedConflictCount() => StmTransaction.UnresolvedConflictCount;

        // -------- Legacy generic overloads (type argument is ignored) --------

        /// <summary>Resets all global counters. The type argument is ignored.</summary>
        public static void Reset<T>() => StmTransaction.ResetCounters();

        /// <summary>Total number of detected conflicts. The type argument is ignored.</summary>
        public static int GetConflictCount<T>() => StmTransaction.ConflictCount;

        /// <summary>Total number of retry attempts. The type argument is ignored.</summary>
        public static int GetRetryCount<T>() => StmTransaction.RetryCount;

        /// <summary>Total number of unresolved conflicts. The type argument is ignored.</summary>
        public static int GetUnresolvedConflictCount<T>() => StmTransaction.UnresolvedConflictCount;
    }
}
