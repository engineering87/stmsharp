// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core
{
    /// <summary>
    /// STMEngine provides methods to execute atomic operations within 
    /// Software Transactional Memory (STM) using a retry and backoff strategy.
    /// 
    /// This class allows for configurable retry attempts and backoff timings, 
    /// enabling robust handling of transaction conflicts in concurrent environments.
    /// 
    /// Key Features:
    /// - Implements an exponential backoff mechanism to minimize contention.
    /// - Provides default configurations, with the flexibility to override as needed.
    /// 
    /// Usage Example:
    /// <code>
    /// await STMEngine.Atomic<int>(transaction =>
    /// {
    ///     var value = transaction.Read(sharedVar);
    ///     transaction.Write(sharedVar, value + 1);
    /// }, maxAttempts: 5, initialBackoffMilliseconds: 200);
    /// </code>
    /// </summary>
    public class STMEngine
    {
        private const int DefaultMaxAttempts = 3;
        private const int DefaultInitialBackoffMilliseconds = 100;

        /// <summary>
        /// Executes an atomic block of code with retry and backoff strategy in case of conflict.
        /// </summary>
        /// <typeparam name="T">The type of the STM transaction variable.</typeparam>
        /// <param name="action">The action to execute inside the transaction.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts (default is 3).</param>
        /// <param name="initialBackoffMilliseconds">The initial backoff time in milliseconds (default is 100).</param>
        public static async Task Atomic<T>(
            Action<Transaction<T>> action, 
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        {
            var transaction = new Transaction<T>();  // Specify the generic type T
            int attempt = 0;
            int backoffTime = initialBackoffMilliseconds;

            while (attempt < maxAttempts)
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
                    await Task.Delay(backoffTime);

                    // Exponential backoff (doubling delay time)
                    backoffTime *= 2;
                    attempt++;
                }
            }
        }

        /// <summary>
        /// Overload for synchronous actions.
        /// </summary>
        /// <typeparam name="T">The type of the STM transaction variable.</typeparam>
        /// <param name="action">The action to execute inside the transaction.</param>
        //public static Task Atomic<T>(Action<Transaction<T>> action)
        //{
        //    return Atomic<T>(transaction =>
        //    {
        //        action(transaction);
        //        return Task.CompletedTask;
        //    });
        //}

        /// <summary>
        /// Executes an atomic block of code with retry and backoff strategy in case of conflict.
        /// Accepts a Func for asynchronous actions.
        /// </summary>
        /// <typeparam name="T">The type of the STM transaction variable.</typeparam>
        /// <param name="func">The func to execute inside the transaction.</param>
        //public static async Task Atomic<T>(Func<Transaction<T>, Task> func)
        //{
        //    var transaction = new Transaction<T>();
        //    int attempt = 0;
        //    int backoffTime = InitialBackoffMilliseconds;

        //    while (attempt < MaxAttempts)
        //    {
        //        try
        //        {
        //            // Execute the action inside the transaction
        //            await func(transaction);

        //            // Attempt to commit the transaction
        //            bool commitSuccess = transaction.Commit();
        //            if (commitSuccess)
        //            {
        //                // Commit successful, exit the loop
        //                break;
        //            }
        //            else
        //            {
        //                // Apply backoff strategy (exponential backoff)
        //                await Task.Delay(backoffTime);

        //                // Exponential backoff (doubling delay time)
        //                backoffTime *= 2;
        //                attempt++;
        //            }
        //        }
        //        catch
        //        {
        //            // Handle exceptions in the action if needed (e.g., rollback logic)
        //        }
        //    }
        //}
    }
}
