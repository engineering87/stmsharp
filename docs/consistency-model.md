# STMSharp Consistency Model

This document specifies the guarantees that STMSharp provides, the conditions under
which they hold, and the boundaries that callers must respect. It is the normative
reference for the library's behavior. Where the code and this document disagree, the
disagreement is a defect to be reconciled, not a license to assume either one.

## Scope

STMSharp implements optimistic Software Transactional Memory for in-process shared
state. The unit of atomicity is a single call to `STMEngine.Atomic`, whose delegate
reads and writes `STMVariable<T>` instances through an `ITransaction` context. The
guarantees below concern those transactions and the variables they touch. They do not
extend to state outside STM variables, nor across process boundaries.

## Protocol

STMSharp uses a TL2-style protocol (Transactional Locking II).

Each `STMVariable<T>` holds its value together with a single 64-bit versioned
write-lock word. Bit 0 is the lock flag and the remaining bits hold the version, so
one atomic read of the word observes both the lock state and the version. Versions are
stamps drawn from a process-wide monotonic clock, the `GlobalVersionClock`, so they are
totally ordered and comparable across all variables.

A transaction samples the clock once at start; this is its read version. Every
transactional read is validated against that read version. A read-write transaction
commits by locking its write set in a deterministic total order, advancing the clock to
obtain a write version, revalidating its read set against the live word, then publishing
its buffered values and stamping each written variable with the write version.

## Guarantees

### G1. Atomicity

A transaction's writes become visible all at once or not at all. If the transaction
commits, every buffered write is published under the write version. If it aborts, no
write is published. There is no state in which a strict subset of a transaction's writes
is observable by another transaction.

### G2. Serializability

Committed transactions are equivalent to some sequential execution. The commit-time
revalidation of the read set against the global version order rejects any transaction
whose read set was invalidated by a concurrent commit, which is what excludes the
non-serializable interleavings that would otherwise lose updates.

### G3. Opacity

A transaction in progress observes only a consistent snapshot, even if it is destined to
abort. A read that would expose a value newer than the read version, or a value being
published by a concurrent committer, does not return that value. Instead it raises an
internal retry signal that unwinds the delegate, and the engine retries the whole
transaction. Consequently user code inside a transaction never computes on a torn or
mutually inconsistent set of values, so it cannot be driven into undefined behavior
(division by an impossible zero, an index derived from a phantom length, and so on)
by concurrency.

### G4. Read-your-own-writes

Within a transaction, reading a variable that the same transaction has already written
returns the pending written value, not the last committed value.

### G5. Deadlock freedom of commit

The commit protocol acquires write-set locks in a deterministic total order by a stable
per-variable identifier, and on failure releases exactly the locks it has acquired, in
reverse. Two committing transactions therefore cannot hold-and-wait in a cycle. Progress
under contention is governed by the retry budget and backoff, not by lock ordering.

## Isolation level

The isolation level is serializable, strengthened to opacity for in-progress
transactions. Read-only transactions, and read-write transactions with an empty write
set, are validated entirely by their reads against the start version and require no
commit-time work.

## Failure and retry semantics

- A transaction that observes an inconsistent snapshot during execution aborts and is
  retried automatically.
- A commit that fails read-set revalidation or write-set lock acquisition aborts and is
  retried automatically.
- Retries are bounded by `MaxAttempts`. When the budget is exhausted, the engine throws
  `TransactionConflictException`. A caller that must not drop the operation should catch
  it and re-enter the atomic block.
- Backoff between retries is two-phase: the first retries use a bounded sub-millisecond
  CPU spin with a cooperative yield and no timer, and only sustained contention reaches
  the configured timed ladder. This is a performance property, not a correctness one;
  correctness does not depend on the backoff strategy.
- The transactional delegate may run more than once. It must therefore be free of
  irreversible side effects, or those side effects must be idempotent. STMSharp does not
  roll back effects outside STM variables.

## Boundaries and caveats

### B1. Mutable reference types

If `T` is a mutable reference type and code mutates the referenced object in place
without going through `Write`, the variable's version does not change, so the mutation is
invisible to conflict detection and breaks isolation. Use immutable values, or treat the
value as immutable and always replace it through `Write`.

### B2. Direct non-transactional `Write(T)`

`STMVariable<T>.Write(T)` follows the same lock protocol as a transactional commit, so it
is safe with respect to concurrent transactions and will not corrupt the version-lock
word. It does not, however, participate in transactional composition or conflict
semantics: it is a single-variable publish. Use it for initialization or for genuinely
independent single-variable updates, not as a substitute for a transaction that needs to
be atomic with other operations.

### B3. Single-flow transactions

An `ITransaction` (or `ITransaction<T>`) instance is not thread-safe. The delegate must
issue its `Read` and `Write` calls from a single logical flow. Sharing one transaction
context across concurrent threads corrupts its read and write sets. Concurrency is
provided across distinct transactions, not within one.

### B4. Transactional dictionary granularity

`TransactionalDictionary<TKey, TValue>` gives each present key its own value cell, so
updating the values of different existing keys does not conflict and is O(1). Membership
is governed by a single structural directory variable. Insertion and removal are
structural: they copy the directory, are O(n) in the number of keys, and conflict with
any concurrent operation that observed the directory, including another structural
mutation. Membership observations are validated, so a transaction that observed a key's
absence aborts if another transaction inserts that key and commits first; this prevents
phantom reads. A finer, per-bucket structural scheme is intentionally out of scope.

### B5. Version clock range

The global version clock is a 64-bit counter advanced once per committing read-write
transaction. Exhausting its range would require on the order of 10^18 commits and is not
a practical concern; it is noted here only for completeness.

## What is not guaranteed

- No guarantee extends to state outside STM variables. STMSharp does not make arbitrary
  side effects transactional.
- No nesting semantics beyond flat composition within a single `Atomic` call are
  specified. A transaction is not currently composed of independently aborting
  sub-transactions.
- No blocking composition operators (`retry` / `orElse`) are specified at this revision.
  They are planned and will extend this document when introduced.
- No fairness guarantee is made among contending transactions. Under sustained contention
  some transactions may exhaust their retry budget while others commit.

## Revision

This document describes the 3.0 line of STMSharp. Changes to the guarantees above are
breaking changes and must be reflected here and in the changelog.
