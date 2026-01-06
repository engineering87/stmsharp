# STMSharp - Software Transactional Memory (STM) for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Nuget](https://img.shields.io/nuget/v/STMSharp?style=plastic)](https://www.nuget.org/packages/STMSharp)
![NuGet Downloads](https://img.shields.io/nuget/dt/STMSharp)
[![issues - stmsharp](https://img.shields.io/github/issues/engineering87/stmsharp)](https://github.com/engineering87/stmsharp/issues)
[![stars - stmsharp](https://img.shields.io/github/stars/engineering87/stmsharp?style=social)](https://github.com/engineering87/stmsharp)

**STMSharp** brings **Software Transactional Memory** to .NET: write your concurrent logic as atomic transactions over shared variables, with optimistic snapshots and a lock-free CAS commit that prevents lost updates under contention.

## Features

- **Transaction-based memory model:** manage and update shared variables without explicit locks.
- **Atomic transactions:** automatic retries with configurable max attempts.
- **Conflict detection:** optimistic snapshot validation that preserves consistency.
- **Configurable backoff strategies:** `Exponential`, `ExponentialWithJitter` (default), `Linear`, `Constant`.
- **Read-only transactions:** validate snapshots without allowing writes, for safer read-heavy workloads.
- **Diagnostics:** global conflict/retry counters per `Transaction<T>` via `StmDiagnostics`.

## What is Software Transactional Memory (STM)?

Software Transactional Memory (STM) is a concurrency control mechanism that simplifies writing concurrent programs by providing an abstraction similar to database transactions. STM allows developers to work with shared memory without the need for explicit locks, reducing the complexity of concurrent programming.

## Key Concepts of STM

- **Transactions:** operations on shared variables are grouped into transactions. A transaction is a unit of work that must be executed atomically.
- **Atomicity:** a transaction is executed as a single, indivisible operation. Either all operations within the transaction are completed, or none are.
- **Isolation:** transactions are isolated from each other, even when running concurrently.
- **Conflict detection:** STM tracks changes to shared variables and detects conflicts when multiple transactions try to modify the same variable.
- **Composability:** STM transactions can be composed and nested, making it easier to build complex operations.

## Benefits of STM

- **Simplified concurrency control:** no low-level locking, fewer deadlocks and race conditions.
- **Scalability:** better behaviour than lock-based systems under high contention.
- **Composability and modularity:** complex operations can be built from smaller transactional pieces.

In STMSharp, STM is implemented using transactions that read from and write to STM variables. Transactions can be automatically retried using a backoff strategy to handle conflicts, making it easier to work with shared data in concurrent environments.

## How it works (in a nutshell)

- `STMVariable<T>` stores a value and a monotonic version (`long`).
- A transaction keeps:
  - `_reads` (cache, including read-your-own-writes),
  - `_writes` (buffered updates),
  - `_snapshotVersions` (immutable version per first observation).

Commit protocol (lock-free):

1. **Guard:** each write must have a captured snapshot.
2. **Reserve:** for each write, CAS the version from `even → odd` (based on the immutable snapshot) via `TryAcquireForWrite`.
3. **Re-validate:** for read-set entries, the current version must still equal the snapshot and be **even** (not reserved).
4. **Write & release:** apply buffered values and increment the version `odd → even` (commit complete).

This ensures serializability and prevents lost updates without runtime locks.

## Core Components

1. **`STMVariable<T>`**  
   Encapsulates a shared value and its version. Supports:
   - transactional access via `ReadWithVersion()` / `Version`,
   - direct writes via `Write(T)` that are **protocol-compatible** (they reserve `even → odd` and release `odd → even`), see caveats below.

2. **`Transaction<T>`**  
   Internal transactional context used by `STMEngine`. Tracks:
   - read cache (`_reads`),
   - buffered writes (`_writes`),
   - immutable snapshot versions (`_snapshotVersions`),
   and implements the optimistic commit protocol.

3. **`STMEngine`**  
   Public façade exposing `Atomic<T>(...)` overloads, with:
   - configurable retry/backoff,
   - support for read-only and read-write modes,
   - overloads that accept `StmOptions`.

4. **`StmOptions`**  
   Immutable configuration for transactional execution:
   - `MaxAttempts`
   - `BaseDelay`, `MaxDelay`
   - `BackoffType`
   - `TransactionMode` (`ReadWrite`, `ReadOnly`)

5. **`StmDiagnostics`**  
   Public diagnostics helper:
   - `GetConflictCount<T>()`
   - `GetRetryCount<T>()`
   - `Reset<T>()`

Counters are per closed generic type (`Transaction<int>` vs `Transaction<string>`).

## CAS & the internal protocol

STMSharp uses an even/odd version scheme and Compare-And-Exchange (CAS) to coordinate writers:

- **Invariants**
    - Even version ⇒ variable is free (no writer holds a reservation).
    - Odd version ⇒ variable is reserved by some writer (during a commit attempt or a direct write that follows the same protocol).
    - Transactional commits are the recommended way to mutate shared state under concurrency; direct writes are protocol-compatible but bypass transactional composition and conflict semantics (use with care under contention).
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
    - All reservations over the write-set are attempted in a stable total order (by a per-variable unique id) to reduce livelock under contention.
    - On failure, only the already acquired reservations are released, in reverse order.
- **Snapshots**
    - The first observation of a variable (read or write-first) captures an immutable `(value, version)` pair used both for validation and reservation.

## How to use it

**Basic example**

```csharp
// Initialize a shared STM variable
var sharedVar = new STMVariable<int>(0);

// Perform an atomic transaction to increment the value
await STMEngine.Atomic<int>(tx =>
{
    var value = tx.Read(sharedVar);
    tx.Write(sharedVar, value + 1);
});

// Perform another atomic transaction
await STMEngine.Atomic<int>(tx =>
{
    var value = tx.Read(sharedVar);
    tx.Write(sharedVar, value + 1);
});
```

**Using StmOptions and read-only mode**

```csharp
var sharedVar = new STMVariable<int>(0);

// Read-only transaction (throws if Write is called)
var readOnlyOptions = StmOptions.ReadOnly;

await STMEngine.Atomic<int>(async tx =>
{
    var value = tx.Read(sharedVar);
    Console.WriteLine($"Current value: {value}");
    // tx.Write(sharedVar, 123); // would throw InvalidOperationException
}, readOnlyOptions);

// Custom retry/backoff policy
var customOptions = new StmOptions(
    MaxAttempts: 5,
    BaseDelay: TimeSpan.FromMilliseconds(50),
    MaxDelay: TimeSpan.FromMilliseconds(1000),
    Strategy: BackoffType.ExponentialWithJitter,
    Mode: TransactionMode.ReadWrite
);

await STMEngine.Atomic<int>(async tx =>
{
    var value = tx.Read(sharedVar);
    tx.Write(sharedVar, value + 1);
}, customOptions);
```

**Diagnostics**

```csharp
// Reset counters for int-transactions
StmDiagnostics.Reset<int>();

// Run some atomic operations...
var conflicts = StmDiagnostics.GetConflictCount<int>();
var retries   = StmDiagnostics.GetRetryCount<int>();

Console.WriteLine($"Conflicts: {conflicts}, Retries: {retries}");
```

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
- **Backoff Time**: The delay applied in case of conflicts, with configurable backoff strategies (Exponential, Exponential+Jitter, Linear, Constant).

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
