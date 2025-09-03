# STMSharp - Software Transactional Memory (STM) for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Nuget](https://img.shields.io/nuget/v/STMSharp?style=plastic)](https://www.nuget.org/packages/STMSharp)
![NuGet Downloads](https://img.shields.io/nuget/dt/STMSharp)
[![issues - stmsharp](https://img.shields.io/github/issues/engineering87/stmsharp)](https://github.com/engineering87/stmsharp/issues)
[![stars - stmsharp](https://img.shields.io/github/stars/engineering87/stmsharp?style=social)](https://github.com/engineering87/stmsharp)

**STMSharp** brings **Software Transactional Memory** to .NET: write your concurrent logic as atomic transactions over shared variables, with optimistic snapshots and a lock-free CAS commit that prevents lost updates under contention.

## Features
- **Transaction-based memory model:** Manage and update shared variables without needing locks.
- **Atomic transactions:** Supports atomicity with retry mechanisms and backoff strategies.
- **Conflict detection:** Automatically detects conflicts in the transaction, ensuring data consistency.
- **Exponential backoff:** Includes an automatic backoff strategy for retries, enhancing performance in high-contention scenarios.

⚠️ **Breaking change**: `Version` is now `long` and `ReadWithVersion()` returns `(T Value, long Version)`.

## What is Software Transactional Memory (STM)?
Software Transactional Memory (STM) is a concurrency control mechanism that simplifies writing concurrent programs by providing an abstraction similar to database transactions. STM allows developers to work with shared memory without the need for explicit locks, reducing the complexity of concurrent programming.

## Key Concepts of STM:
- **Transactions:** Operations on shared variables are grouped into transactions. A transaction is a unit of work that must be executed atomically.
- **Atomicity:** A transaction is executed as a single, indivisible operation. Either all operations within the transaction are completed, or none are, ensuring consistency.
- **Isolation:** Transactions are isolated from each other, meaning that the operations in one transaction do not interfere with others, even if they are executed concurrently.
- **Conflict Detection:** STM systems track changes to shared variables and detect conflicts when two or more transactions try to modify the same variable. If a conflict is detected, the system retries the transaction or resolves it according to a conflict resolution strategy.
- **Composability:** STM transactions can be nested or composed together, making it easier to structure complex operations.

## Benefits of STM:
- **Simplified concurrency control:** STM eliminates the need for low-level synchronization mechanisms like locks, reducing the potential for deadlocks and race conditions.
- **Scalability:** STM can scale more effectively than traditional lock-based systems, especially in highly concurrent environments.
- **Composability and Modularity:** STM makes it easier to compose complex operations from simple ones, which promotes cleaner and more modular code.

In STMSharp, STM is implemented using transactions that read from and write to STM variables. Transactions can be retried automatically using an exponential backoff strategy to handle conflicts, making it easier to work with shared data in concurrent environments.

## How it works (in a nutshell)
- `STMVariable<T>` stores a value and a monotonic version (`long`).
 -A transaction keeps:
    - `_reads` (cache, includes read-your-own-writes),
    - `_writes` (buffered updates),
    - `_snapshotVersions` (immutable version per first observation).
- Commit protocol (lock-free):
    1. Guard: each write must have a snapshot.
    2. Reserve each write via CAS: `even → odd` (TryAcquireForWrite).
    3. Re-validate read-only entries: current version must equal the snapshot and be even (not reserved).
    4. Write & release: apply buffered values and increment version `odd → even`.

This ensures serializability and prevents lost updates without runtime locks.

## Core Components:
1. **Transaction<T>:** The main class representing a transaction. It allows reading from and writing to STM variables.
2. **STMVariable<T>:** A type that encapsulates the shared data and supports STM operations (read/write).
3. **STMEngine:** Provides static methods for managing transactions and conflict resolution.

## CAS & the internal protocol
STMSharp uses an even/odd version scheme and Compare-And-Exchange (CAS) to coordinate writers:
- **Invariants**
    - Even version ⇒ variable is free (no writer holds a reservation).
    - Odd version ⇒ variable is reserved by some writer during a commit attempt.
    - Only the transactional path mutates state; non-transactional writes are not exposed.
- **Reserve (CAS)**
    ```csharp
    // success only if current == snapshotVersion (even)
    // sets version to snapshotVersion + 1 (odd), meaning "reserved"
    Interlocked.CompareExchange(ref version, snapshotVersion + 1, snapshotVersion);
    ```
- **Revalidation**
    - For each read-set entry: `currentVersion == snapshotVersion` and `(currentVersion & 1) == 0`.
    - For each write-set entry: already reserved by the current commit; skip.
- **Write & release**
    - Write the new value, then `Interlocked.Increment(ref version)` to turn `odd → even` (commit complete).
    - On abort, `ReleaseAfterAbort()` also increments once to revert `odd → even`.
- **Deterministic ordering**
    - All reservations over the write-set are attempted in a stable order (based on reference identity) to reduce livelock under contention.
    - On failure, only the already acquired reservations are released, in reverse order.
- **Snapshots**
    - The first observation of a variable (read or write-first) captures an immutable `(value, version)` pair used both for validation and reservation.
- **Why no non-transactional writes?**
    - To preserve the invariants and prevent out-of-band mutations from violating the even/odd protocol, `ISTMVariable<T>` exposes only `ReadWithVersion()` and `Version`. Writes occur exclusively via the transactional commit path.

## How to use it
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
## 📦 Core Classes

### `ISTMVariable<T>` (interface)
Represents a shared STM variable within the system.

| Member | Description |
|--------|-------------|
| `ReadWithVersion()` | Atomically reads the value and version. |
| `Write(T value)` | Atomically writes a new value. |
| `int Version { get; }` | Gets the current version of the variable. |
| `IncrementVersion()` | Increments the version manually. |

> ⚠️ Intended for internal STM operations only, not exposed to user code directly.

---

### `STMVariable<T>` : `ISTMVariable<T>`
Concrete implementation of a **thread-safe STM variable** using `Volatile` and `Interlocked`.

| Field/Method | Description |
|--------------|-------------|
| `_boxedValue` | Internal boxed value to support both value and reference types. |
| `Read()` | Simple thread-safe read. |
| `Write(T value)` | Writes the new value and increments the version. |
| `ReadWithVersion()` | Returns a consistent snapshot of value and version. |
| `Version` / `IncrementVersion()` | Handles versioning for conflict detection. |

> ✅ Used as the shared state managed inside transactions.

---

### `Transaction<T>`
Represents an atomic unit of work. Implements **pessimistic isolation** and conflict detection via version locking.

| Field | Description |
|-------|-------------|
| `_reads` | Cache of read values. |
| `_writes` | Pending writes to apply at commit time. |
| `_lockedVersions` | Versions locked during reads to check for conflicts. |
| `Read(...)` | Reads from STM variable and locks its version. |
| `Write(...)` | Records an intended write to apply later. |
| `CheckForConflicts()` | Verifies if any STM variable has changed since read. |
| `Commit()` | Applies writes if no conflicts are detected. |
| `ConflictCount` / `RetryCount` | Static counters for diagnostics. |

> ♻️ A new transaction is created on each attempt (controlled by `STMEngine`).

---

### `STMEngine`
Coordinates STM execution with **retry and exponential backoff** strategy.

| Method | Description |
|--------|-------------|
| `Atomic<T>(Action<Transaction<T>>)` | Runs a synchronous transactional block. |
| `Atomic<T>(Func<Transaction<T>, Task>)` | Runs an async transactional block. |
| `DefaultMaxAttempts` / `DefaultInitialBackoffMilliseconds` | Default retry/backoff configuration. |

> 🔁 Retries the transaction on conflict, doubling delay after each failure.

---

## 📈 Performance Benchmarks
Detailed performance measurements were conducted using [BenchmarkDotNet](https://benchmarkdotnet.org/) to compare variable access and atomic operations under various backoff strategies.

- **Scope**: Execution time, memory allocations, and GC activity
- **Operations**: Write/Read (standard), Atomic Write/Read
- **Strategies**: Exponential, Exponential + Jitter, Linear, Constant

➡️ **[Full benchmark report](docs/benchmarks/benchmarks.md)**

## 📈 Benchmarking
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

## License
STMSharp source code is available under MIT License, see license in the source.

## Contact
Please contact at francesco.delre[at]protonmail.com for any details.
