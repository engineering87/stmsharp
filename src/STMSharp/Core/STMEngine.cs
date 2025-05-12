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
        /// Executes a synchronous action within an STM transaction with retry.
        /// </summary>
        public static async Task Atomic<T>(
            Action<Transaction<T>> action,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        {
            // Wrap the synchronous action into the asynchronous overload
            await Atomic<T>(tx =>
            {
                action(tx);
                return Task.CompletedTask;
            }, maxAttempts, initialBackoffMilliseconds);
        }


        /// <summary>
        /// Executes an asynchronous function within an STM transaction with retry and backoff.
        /// </summary>
        public static async Task Atomic<T>(
            Func<Transaction<T>, Task> func,
            int maxAttempts = DefaultMaxAttempts,
            int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        {
            int attempt = 0;
            int backoffTime = initialBackoffMilliseconds;

            // Retry loop for transaction attempts
            while (attempt < maxAttempts)
            {
                // Create a new transaction instance for each attempt
                var transaction = new Transaction<T>();

                // Execute user-provided transactional logic
                await func(transaction);

                // Attempt to commit: returns true if no conflict detected
                if (transaction.Commit())
                {
                    // Commit successful, exit method
                    return;
                }

                // Conflict detected: wait before retrying
                await Task.Delay(backoffTime);
                // Exponential backoff for next attempt
                backoffTime *= 2;
                attempt++;
            }

            // All attempts failed: throw timeout exception
            throw new TimeoutException($"STM transaction failed after {maxAttempts} attempts");
        }

        /// <summary>
        /// Executes an atomic block of code with retry and backoff strategy in case of conflict.
        /// </summary>
        /// <typeparam name="T">The type of the STM transaction variable.</typeparam>
        /// <param name="action">The action to execute inside the transaction.</param>
        /// <param name="maxAttempts">The maximum number of retry attempts (default is 3).</param>
        /// <param name="initialBackoffMilliseconds">The initial backoff time in milliseconds (default is 100).</param>
        //public static async Task Atomic<T>(
        //    Action<Transaction<T>> action, 
        //    int maxAttempts = DefaultMaxAttempts,
        //    int initialBackoffMilliseconds = DefaultInitialBackoffMilliseconds)
        //{
        //    int attempt = 0;
        //    int backoffTime = initialBackoffMilliseconds;

        //    while (attempt < maxAttempts)
        //    {
        //        var transaction = new Transaction<T>();  // Specify the generic type T

        //        // Execute the action inside the transaction
        //        action(transaction);

        //        // Attempt to commit the transaction
        //        bool commitSuccess = transaction.Commit();
        //        if (commitSuccess)
        //        {
        //            // Commit successful, exit the loop
        //            break;
        //        }
        //        else
        //        {
        //            // Apply backoff strategy (exponential backoff)
        //            await Task.Delay(backoffTime);

        //            // Exponential backoff (doubling delay time)
        //            backoffTime *= 2;
        //            attempt++;
        //        }
        //    }
        //}

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
