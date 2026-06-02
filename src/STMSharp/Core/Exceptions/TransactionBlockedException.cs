// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Exceptions
{
    /// <summary>
    /// Internal signal raised by <see cref="Interfaces.ITransaction.Retry"/> to indicate that
    /// the transaction cannot make progress with its current read snapshot and wishes to block
    /// until one of the variables it has read is changed by another committed transaction.
    ///
    /// This is distinct from <see cref="TransactionRetryException"/>, which signals an
    /// inconsistent snapshot that should be retried immediately. A blocked transaction instead
    /// parks on its read set and is woken by a later commit, then re-executes from the start.
    ///
    /// The exception carries no message and is never expected to escape the engine; callers of
    /// <c>STMEngine.Atomic</c> do not observe it.
    /// </summary>
    internal sealed class TransactionBlockedException : Exception
    {
    }
}
