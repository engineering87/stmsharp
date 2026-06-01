// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Exceptions
{
    /// <summary>
    /// Exception thrown when a transaction cannot be committed, typically because
    /// the configured retry budget (MaxAttempts) has been exhausted due to repeated
    /// optimistic-commit conflicts under contention.
    /// </summary>
    public class TransactionConflictException : Exception
    {
        public TransactionConflictException() { }

        public TransactionConflictException(string message)
            : base(message) { }

        public TransactionConflictException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
