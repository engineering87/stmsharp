# STMSharp - Software Transactional Memory (STM) for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Nuget](https://img.shields.io/nuget/v/STMSharp?style=plastic)](https://www.nuget.org/packages/STMSharp)
![NuGet Downloads](https://img.shields.io/nuget/dt/STMSharp)
[![issues - stmsharp](https://img.shields.io/github/issues/engineering87/stmsharp)](https://github.com/engineering87/stmsharp/issues)
[![stars - stmsharp](https://img.shields.io/github/stars/engineering87/stmsharp?style=social)](https://github.com/engineering87/stmsharp)

STMSharp is a .NET library for lock-free synchronization using Software Transactional Memory (STM), enabling atomic transactions and efficient multi-threading.

## Features
- **Transaction-based memory model:** Manage and update shared variables without needing locks.
- **Atomic transactions:** Supports atomicity with retry mechanisms and backoff strategies.
- **Conflict detection:** Automatically detects conflicts in the transaction, ensuring data consistency.
- **Exponential backoff:** Includes an automatic backoff strategy for retries, enhancing performance in high-contention scenarios.

## What is Software Transactional Memory (STM)?
Software Transactional Memory (STM) is a concurrency control mechanism that simplifies writing concurrent programs by providing an abstraction similar to database transactions. STM allows developers to work with shared memory without the need for explicit locks, reducing the complexity of concurrent programming.

### Key Concepts of STM:
- **Transactions:** Operations on shared variables are grouped into transactions. A transaction is a unit of work that must be executed atomically.
- **Atomicity:** A transaction is executed as a single, indivisible operation. Either all operations within the transaction are completed, or none are, ensuring consistency.
- **Isolation:** Transactions are isolated from each other, meaning that the operations in one transaction do not interfere with others, even if they are executed concurrently.
- **Conflict Detection:** STM systems track changes to shared variables and detect conflicts when two or more transactions try to modify the same variable. If a conflict is detected, the system retries the transaction or resolves it according to a conflict resolution strategy.
- **Composability:** STM transactions can be nested or composed together, making it easier to structure complex operations.

### Benefits of STM:
- **Simplified concurrency control:** STM eliminates the need for low-level synchronization mechanisms like locks, reducing the potential for deadlocks and race conditions.
- **Scalability:** STM can scale more effectively than traditional lock-based systems, especially in highly concurrent environments.
- **Composability and Modularity:** STM makes it easier to compose complex operations from simple ones, which promotes cleaner and more modular code.

In STMSharp, STM is implemented using transactions that read from and write to STM variables. Transactions can be retried automatically using an exponential backoff strategy to handle conflicts, making it easier to work with shared data in concurrent environments.

## How it works
STMSharp implements the Software Transactional Memory (STM) pattern, allowing you to perform read and write operations on shared variables inside transactions. Each transaction reads the values of shared variables, applies updates, and commits them if no conflicts are detected. If a conflict occurs, the transaction will be retried with an exponential backoff mechanism, which gradually increases the delay between retries.

### Core Components:
1. **Transaction<T>:** The main class representing a transaction. It allows reading from and writing to STM variables.
2. **STMVariable<T>:** A type that encapsulates the shared data and supports STM operations (read/write).
3. **STMEngine:** Provides static methods for managing transactions and conflict resolution.

### How to use it
Here's a basic example of how to use STMSharp in your project:

```csharp
try
{
   // Initialize a shared STM variable
   var sharedVar = new STMVariable<int>(0);

   // Perform an atomic transaction to increment the value
   STMEngine.Atomic(transaction =>
   {
       var value = transaction.Read(sharedVar);
       transaction.Write(sharedVar, value + 1);
   });

   // Perform another atomic transaction
   STMEngine.Atomic(transaction =>
   {
       var value = transaction.Read(sharedVar);
       transaction.Write(sharedVar, value + 1);
   });
}
catch (InvalidOperationException ex)
{
    Console.WriteLine("Transaction failed: " + ex.Message);
}
```

## Benchmarking
This project includes a benchmarking application designed to test and simulate the behavior of the STMSharp library under varying conditions. The benchmark is built to analyze the efficiency and robustness of the STM mechanism. The benchmark parameters are configurable through a JSON file named appsettings.json. This allows centralized and flexible management of the values used for testing.

### Purpose of the Benchmark
The goal of the benchmark is to measure the performance of the STMSharp library based on:
- **Number of Threads**: The number of concurrent threads accessing the transactional memory.
- **Number of Operations**: The number of transactions executed by each thread.
- **Backoff Time**: The delay applied in case of conflicts, with an exponential backoff strategy.

### Benchmark Results
At the end of execution, the benchmark provides several statistics:
- **Total Duration**: The total time taken to complete the benchmark.
- **Average Time per Operation**: Calculated as the ratio between the total duration and the total number of operations.
- **Conflicts Resolved**: The total number of conflicts handled by the STM system.
- **Retries Attempted**: The total number of retry attempts made.

## Contributing
Thank you for considering to help out with the source code!
If you'd like to contribute, please fork, fix, commit and send a pull request for the maintainers to review and merge into the main code base.

 * [Setting up Git](https://docs.github.com/en/get-started/getting-started-with-git/set-up-git)
 * [Fork the repository](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/working-with-forks/fork-a-repo)
 * [Open an issue](https://github.com/engineering87/stmsharp/issues) if you encounter a bug or have a suggestion for improvements/features

### Licensee
OpenSharpTrace source code is available under MIT License, see license in the source.

### Contact
Please contact at francesco.delre[at]protonmail.com for any details.
