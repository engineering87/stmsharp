// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Exceptions
{
    /// <summary>
    /// Internal control-flow signal raised when a read observes that the current
    /// snapshot is no longer consistent (the location is locked by another committer
    /// or its version is newer than the transaction's start version). Raising it
    /// unwinds the user delegate so the engine can retry the whole transaction,
    /// which is how opacity is preserved during execution.
    ///
    /// This type is never surfaced to callers: the engine catches it and either
    /// retries or, once the attempt budget is exhausted, throws
    /// <see cref="TransactionConflictException"/>.
    /// </summary>
    internal sealed class TransactionRetryException : Exception
    {
        public TransactionRetryException() { }
    }
}
