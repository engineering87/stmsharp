# STMSharp - Software Transactional Memory (STM) for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Nuget](https://img.shields.io/nuget/v/STMSharp?style=plastic)](https://www.nuget.org/packages/STMSharp)
![NuGet Downloads](https://img.shields.io/nuget/dt/STMSharp)
[![issues - stmsharp](https://img.shields.io/github/issues/engineering87/stmsharp)](https://github.com/engineering87/stmsharp/issues)
[![stars - stmsharp](https://img.shields.io/github/stars/engineering87/stmsharp?style=social)](https://github.com/engineering87/stmsharp)

**STMSharp** brings **Software Transactional Memory** to .NET. You write concurrent logic as atomic transactions over shared variables, and the engine provides a consistent snapshot during execution, validates that snapshot at commit, and retries automatically under contention. There are no explicit locks in user code.

The engine implements a TL2-style protocol (Transactional Locking II). A transaction samples a version from a global clock at start, every read is validated against that version so the transaction always observes a consistent snapshot (opacity), and a read-write transaction commits by locking its write set in a deterministic order, revalidating its read set, then publishing its buffered values and stamping a new version.

## Features

- **Transaction-based memory model:** read and write shared variables without explicit locks.
- **Heterogeneous transactions:** a single transaction can read and write `STMVariable<T>` instances of different element types, because the transactional context is non-generic and its `Read<T>` and `Write<T>` methods are generic per call.
- **Opacity:** a running transaction never observes a torn or inconsistent intermediate state. An inconsistent read aborts and the transaction retries.
- **Atomic commit with conflict detection:** optimistic snapshot validation backed by a versioned write-lock word, with automatic retries up to a configurable budget.
- **Configurable backoff strategies:** `Exponential`, `ExponentialWithJitter` (default), `Linear`, `Constant`. The engine absorbs the first few retries with a sub-millisecond CPU spin and only reaches the timed ladder under sustained contention.
- **Read-only transactions:** validate snapshots without allowing writes, for read-heavy workloads.
- **Transactional dictionary:** `TransactionalDictionary<TKey, TValue>` with fine-grained per-key value concurrency and structural membership validation that prevents phantom reads.
- **Diagnostics:** process-wide conflict, retry, and unresolved-conflict counters via `STMDiagnostics`.

## What is Software Transactional Memory (STM)?

Software Transactional Memory is a concurrency control mechanism that gives shared-memory programming an abstraction similar to database transactions. Operations on shared variables are grouped into a transaction that executes atomically, in isolation from other transactions, and the runtime detects conflicts and retries rather than requiring the programmer to acquire and order locks.

## Key concepts

- **Atomicity:** a transaction either commits all of its writes or none of them.
- **Isolation and opacity:** concurrent transactions do not observe each other's uncommitted state, and even a transaction that will ultimately abort sees only a consistent snapshot while it runs.
- **Optimistic concurrency:** transactions proceed without locking on read, and conflicts are detected at commit. Under contention the engine retries with a backoff strategy.
- **Composability:** several operations, including several transactional dictionary operations, compose inside a single `STMEngine.Atomic` call and commit as one unit.

## How it works

`STMVariable<T>` stores a value together with a 64-bit versioned write-lock word. Bit 0 is the lock flag and the remaining bits hold the version, so a single atomic read observes both the lock state and the version at once. Versions are stamps drawn from a process-wide `GlobalVersionClock`, so they are comparable across all variables.

A transaction keeps two append-only buffers rather than dictionaries: the read set (the distinct variables it has read) and the write set (variables together with their pending values). For the small transactions typical of STM this allocates far less than hashing; lookups are linear in the set size.

Commit protocol for a read-write transaction:

1. **Lock the write set** in a deterministic total order, by a per-variable unique id, to avoid deadlock. Acquisition uses a single compare-and-swap per variable.
2. **Obtain a write version** by advancing the global clock once.
3. **Revalidate the read set** against the live version-lock word. If a read variable is locked by another committer, or its version is newer than the transaction's start version, the commit aborts and releases the locks it holds. The read-set validation is skipped when the write version is exactly one past the start version, because no other commit can have intervened.
4. **Publish and stamp:** write the buffered values, then release each lock while stamping the new version.

Read-only transactions, and read-write transactions with an empty write set, commit with no extra work, because every read was already validated against the start version.

A read that observes an inconsistent snapshot during execution does not wait. It raises an internal retry signal that unwinds the user delegate, and the engine retries the whole transaction. This is how opacity is preserved.

## Core components

1. **`STMVariable<T>`**
   A shared value with a versioned write-lock word. Supports transactional access through the engine, plus `Read()`, `ReadWithVersion()`, `Version`, and a direct, protocol-compatible `Write(T)` (see caveats below).

2. **`ITransaction`**
   The non-generic transactional context passed to `STMEngine.Atomic`. Its `Read<T>(STMVariable<T>)` and `Write<T>(STMVariable<T>, T)` methods are generic per call, so one transaction can span variables of different element types. `Read` returns the transaction's own pending value if the variable was already written in the same transaction (read-your-own-writes). An instance is not thread-safe; concurrency is provided across distinct transactions, not within one.

3. **`ITransaction<T>` (legacy)**
   The single-type context, retained for source compatibility. It delegates to the non-generic core through an internal adapter, so existing code continues to compile and run unchanged. New code should prefer `ITransaction`.

4. **`STMEngine`**
   The public façade. It exposes `Atomic(...)` overloads (synchronous `Action<ITransaction>`, asynchronous `Func<ITransaction, Task>`, and value-returning `Func<ITransaction, Task<TResult>>`), each with either explicit retry and backoff parameters or an `StmOptions` argument, plus the legacy single-type overloads. When the retry budget is exhausted it throws `TransactionConflictException`.

5. **`StmOptions`**
   Immutable configuration: `MaxAttempts`, `BaseDelay`, `MaxDelay`, `Strategy` (`BackoffType`), and `Mode` (`TransactionMode.ReadWrite` or `ReadOnly`). `StmOptions.Default` and `StmOptions.ReadOnly` are provided, and `with` expressions create modified copies.

6. **`STMDiagnostics`**
   Process-wide counters: `GetConflictCount()`, `GetRetryCount()`, `GetUnresolvedConflictCount()`, and `Reset()`. Generic overloads are retained for source compatibility and ignore their type argument.

## Consistency model

STMSharp provides **serializability** and **opacity**. Committed transactions are equivalent to some serial order, and a transaction in progress only ever observes a consistent snapshot, so it cannot be driven into undefined behavior by a concurrent commit before it aborts.

A few boundaries are worth stating explicitly:

- **Mutable reference types.** If `T` is a mutable reference type and code mutates the referenced object without going through `Write`, the version does not change and isolation is broken. Prefer immutable values, or treat `T` as a value and always replace it through `Write`.
- **Direct `Write(T)`.** The non-transactional `Write` on a variable follows the same lock protocol, so it is safe with respect to concurrent transactions, but it bypasses transactional composition and conflict semantics. Use it for initialization or for genuinely independent single-variable updates, not as a substitute for a transaction.
- **Retry of the delegate.** Because a transaction can be retried, the delegate must be free of irreversible side effects, or those side effects must be idempotent.
- **Budget exhaustion.** When a transaction cannot commit within `MaxAttempts`, the engine throws `TransactionConflictException`. Callers that must not drop the operation should catch it and retry the whole atomic block.
- **Transactional dictionary cost.** A value update on an existing key is O(1) and does not conflict with updates to other keys. A structural change (insertion or removal) copies the directory and is O(n) in the number of keys, and it conflicts with any concurrent operation that observed the directory.

## How to use it

**Basic transaction**

```csharp
var sharedVar = new STMVariable<int>(0);

await STMEngine.Atomic(tx =>
{
    var value = tx.Read(sharedVar);
    tx.Write(sharedVar, value + 1);
});
```

**Heterogeneous transaction across element types**

```csharp
var balance = new STMVariable<int>(0);
var name    = new STMVariable<string>("");

await STMEngine.Atomic(tx =>
{
    tx.Write(balance, tx.Read(balance) + 10);
    tx.Write(name, "updated");
});
```

**Returning a value**

```csharp
var account = new STMVariable<int>(100);

int balance = await STMEngine.Atomic(async tx =>
{
    await Task.Yield();
    return tx.Read(account);
});
```

The value-returning overload takes an asynchronous body, `Func<ITransaction, Task<TResult>>`. A synchronous-result overload is intentionally not offered, because it is ambiguous for any lambda that returns a `Task`. For a synchronous transaction that produces a value, capture it through the void overload:

```csharp
int balance = 0;
await STMEngine.Atomic(tx => { balance = tx.Read(account); });
```

**Read-only mode and custom options**

```csharp
var sharedVar = new STMVariable<int>(0);

// Read-only transaction (throws if Write is called)
await STMEngine.Atomic(tx =>
{
    var value = tx.Read(sharedVar);
    Console.WriteLine($"Current value: {value}");
}, StmOptions.ReadOnly);

// Custom retry and backoff policy
var options = new StmOptions(
    MaxAttempts: 5,
    BaseDelay: TimeSpan.FromMilliseconds(50),
    MaxDelay: TimeSpan.FromMilliseconds(1000),
    Strategy: BackoffType.ExponentialWithJitter,
    Mode: TransactionMode.ReadWrite);

await STMEngine.Atomic(tx =>
{
    var value = tx.Read(sharedVar);
    tx.Write(sharedVar, value + 1);
}, options);
```

**Transactional dictionary**

```csharp
var dict = new TransactionalDictionary<string, int>();

await STMEngine.Atomic(tx =>
{
    dict.Set(tx, "a", 1);
    dict.Set(tx, "b", 2);
});

int a = 0;
await STMEngine.Atomic(tx => a = dict.Get(tx, "a"));
```

**Diagnostics**

```csharp
STMDiagnostics.Reset();

// Run some atomic operations...

var conflicts = STMDiagnostics.GetConflictCount();
var retries   = STMDiagnostics.GetRetryCount();
var unresolved = STMDiagnostics.GetUnresolvedConflictCount();

Console.WriteLine($"Conflicts: {conflicts}, Retries: {retries}, Unresolved: {unresolved}");
```

## Backoff and contention

When a commit fails because of a conflict, the engine waits before retrying. The first retries are absorbed by a bounded CPU spin and a single cooperative yield, both on the microsecond scale and free of any operating-system timer. Only sustained contention reaches the configured timed ladder. This matters because a sub-quantum `Task.Delay` is rounded up to the system timer tick (about 15 ms on Windows), so a naive timed backoff would make a single conflict cost milliseconds. Pushing the early retries onto a spin keeps contended transactions on the microsecond scale.

`ExponentialWithJitter` is full-jitter: the delay is uniform in the range up to the capped exponential value, which breaks synchronized retry storms across concurrent transactions.

## Performance benchmarks

Performance is measured with [BenchmarkDotNet](https://benchmarkdotnet.org/), covering execution time, allocations, and GC activity for read and write operations, atomic operations, and the transactional dictionary, across the four backoff strategies.

See the [full benchmark report](docs/benchmarks/benchmarks.md).

## Contributing

If you would like to contribute, please fork the repository, make your changes, and open a pull request for review.

 * [Setting up Git](https://docs.github.com/en/get-started/getting-started-with-git/set-up-git)
 * [Fork the repository](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/working-with-forks/fork-a-repo)
 * [Open an issue](https://github.com/engineering87/stmsharp/issues) if you encounter a bug or have a suggestion

## License

STMSharp source code is available under the MIT License. See the license in the source.

## Contact

Please contact francesco.delre[at]protonmail.com for any details.
