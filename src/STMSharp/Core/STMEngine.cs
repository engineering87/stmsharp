// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    public class STMEngine
    {
        private const int MaxAttempts = 3;
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
                // Execute the action inside the transaction
                action(transaction);

                // Attempt to commit the transaction
                bool commitSuccess = transaction.Commit();
                if (commitSuccess)
                {
                    // Commit successful, exit the loop
                    break;
                }
                else
                {
                    // Apply backoff strategy (exponential backoff)
                    Console.WriteLine($"Retrying in {backoffTime}ms...");
                    Thread.Sleep(backoffTime);

                    // Exponential backoff (doubling delay time)
                    backoffTime *= 2;
                    attempt++;
                }
            }
        }
    }
}
