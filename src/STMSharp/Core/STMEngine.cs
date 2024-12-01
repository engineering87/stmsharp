// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Exceptions;

namespace STMSharp.Core
{
    public class STMEngine
    {
        private const int MaxAttempts = 5;
        private const int InitialBackoffMilliseconds = 100;

        /// <summary>
        /// Executes an atomic block of code with retry and backoff strategy in case of conflict.
        /// </summary>
        /// <typeparam name="T">The type of the STM transaction variable.</typeparam>
        /// <param name="action">The action to execute inside the transaction.</param>
        public static void Atomic<T>(Action<Transaction<T>> action)
        {
            var transaction = new Transaction<T>();  // Specify the generic type T
            int attempt = 0;
            int backoffTime = InitialBackoffMilliseconds;

            while (attempt < MaxAttempts)
            {
                try
                {
                    // Execute the action inside the transaction
                    action(transaction);

                    // Attempt to commit the transaction
                    transaction.Commit(); // Commit does not return a value, but may throw on failure
                    break; // Commit successful, exit the loop
                }
                catch (TransactionConflictException ex)
                {
                    // Handle commit failure due to conflict
                    Console.WriteLine($"Commit failed due to conflict: {ex.Message}");

                    // Apply backoff strategy (exponential backoff)
                    Console.WriteLine($"Retrying in {backoffTime}ms...");
                    System.Threading.Thread.Sleep(backoffTime);

                    // Exponential backoff (doubling delay time)
                    backoffTime *= 2;
                    attempt++;
                }
                catch (Exception ex)
                {
                    // Handle unexpected exceptions (log or rethrow)
                    Console.WriteLine($"An unexpected error occurred: {ex.Message}");
                    throw;
                }
            }

            if (attempt == MaxAttempts)
            {
                throw new InvalidOperationException("Transaction failed after maximum retry attempts.");
            }
        }
    }
}
