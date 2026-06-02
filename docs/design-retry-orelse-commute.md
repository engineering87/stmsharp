# Design note: retry / orElse / commutative operations

Status: design only. This note is the proposal to be reviewed before any code is
written. None of it is implemented yet. Because it changes the concurrency core, every
increment must be compiled and tested locally, one at a time, before being trusted.

## Motivation

STMSharp currently offers atomic transactions with automatic conflict retry. What
separates a mature STM from an atomic-commit mechanism is composable blocking and
conflict reduction:

- `retry` lets a transaction declare that it cannot proceed yet and should block until
  the state it read changes, instead of spinning or returning a sentinel.
- `orElse` composes two alternatives: try the first, and if it blocks with `retry`, try
  the second, committing the first that does not block.
- Commutative operations let independent updates that commute (counter increments, set
  insertions) avoid conflicting with one another, which is the single largest source of
  needless aborts under contention.

These are the features an adopter coming from a mature STM implementation will look
for when deciding whether STMSharp is a complete library.

## Part 1: retry

### Semantics

When a transaction calls `tx.Retry()`, it abandons the current attempt and blocks until
at least one variable in its current read set is committed by another transaction, then
re-executes from the start. This is condition synchronization without a lock and without
busy-waiting: the canonical example is a bounded buffer consumer that calls `Retry` when
the buffer is empty and is woken when a producer commits an item.

`Retry` with an empty read set would block forever, because nothing could ever wake it.
That case must throw `InvalidOperationException` rather than deadlock silently.

### Mechanism

The existing abort path already unwinds the delegate through an internal exception
(`TransactionRetryException`) caught by `RunCoreAsync`. `Retry` introduces a distinct
signal, for example `TransactionBlockedException`, so the engine can tell "conflict,
retry now" apart from "blocked, wait for a wake-up".

Wake-up requires a per-variable wait registry. The minimal viable design:

- Each `STMVariable<T>` gains an optional wait set, lazily allocated, holding wake
  handles (a `TaskCompletionSource` or a lightweight `ManualResetEventSlim` wrapper) of
  transactions blocked on it.
- On `Retry`, the engine registers one wake handle against every variable in the read
  set, then awaits the handle. To avoid a lost wake-up, registration must happen and the
  read-set versions must be rechecked before the await actually parks: if any read
  variable has already advanced past the read version since the block decision, the
  engine does not park and retries immediately.
- On commit, after publishing and stamping, a committer signals the wait handles of the
  variables it wrote. Signaling must be best-effort and must not run user code while
  holding any lock.

### Cost and risks

The wait registry adds a field to every variable, though lazily, so idle variables stay
cheap. The lost-wake-up window is the hard part and is exactly why this must be built and
tested incrementally, with a dedicated stress test (a producer/consumer queue that would
hang on a lost wake-up). A timeout parameter on the blocking wait is advisable as a
safety valve so a defect degrades to a slow retry rather than a permanent hang.

### API sketch

```csharp
public interface ITransaction
{
    T Read<T>(STMVariable<T> variable);
    void Write<T>(STMVariable<T> variable, T value);
    void Retry();                       // new: block until a read-set variable changes
}
```

The engine's `RunCoreAsync` gains a branch: catch the blocked signal, register waits,
await (with the configured or a default timeout and the cancellation token), then loop.

## Part 2: orElse

### Semantics

`orElse(first, second)` runs `first`. If `first` completes without blocking, its result
stands and `second` is not run. If `first` blocks via `Retry`, its tentative effects are
discarded and `second` runs in the same transaction. If `second` also blocks, the whole
composition blocks on the union of both read sets.

### Mechanism

`orElse` requires the transaction to checkpoint and roll back the read and write sets to
the state they had before `first` ran, so that a blocked `first` leaves no trace before
`second` runs. With the append-only buffers this is cheap: record the read count and
write count on entry, and on a blocked `first` truncate both buffers back to the recorded
counts. The union of read sets needed for the final block is simply whatever remains
registered after `second` also blocks, which the truncation model produces naturally
because the second alternative's reads are appended after the first's were truncated.

The subtlety is that read-your-own-writes within `first` must not leak into `second`.
Truncating the write set to the recorded count handles this, provided no variable written
only by `first` is later read by `second` expecting the pre-`first` value; truncation
guarantees it sees the committed value, which is correct.

### API sketch

```csharp
public static class Stm
{
    // Both alternatives operate on the same transaction; the engine manages checkpoints.
    public static void OrElse(ITransaction tx, Action<ITransaction> first, Action<ITransaction> second);
}
```

An instance method on `ITransaction` is an alternative surface; a static helper keeps the
checkpoint/rollback logic inside the engine where it can see the concrete
`StmTransaction`.

## Part 3: commutative operations

### Semantics

A commutative operation declares that its effect on a variable commutes with other
operations of the same kind, so the engine may apply it at commit without treating an
intervening commit by another commuting transaction as a conflict. The classic case is an
integer counter: two `Add(+1)` operations produce the same result in either order, so
they need not conflict even though both write the same variable.

### Mechanism

This is the most invasive of the three and should come last. A commuting write is not
buffered as "the new value" but as "a function to apply to the committed value at commit
time". At commit, instead of revalidating that the variable is unchanged, the engine
locks the variable, reads the current committed value, applies the buffered function, and
publishes the result. Correctness requires that the operation genuinely commutes and that
it is applied exactly once, which the lock-and-apply-under-lock step provides.

The read set must distinguish a plain read (which still requires version validation) from
a commuting accumulation (which does not). A variable that is both read normally and
accumulated commutatively in the same transaction falls back to the conservative
validated path.

### API sketch

```csharp
public interface ITransaction
{
    // Buffer a commuting update; applied to the committed value under lock at commit.
    void Commute<T>(STMVariable<T> variable, Func<T, T> operation);
}
```

### Risks

Commute weakens the validation invariant deliberately, so it is the easiest place to
introduce a subtle serializability violation. It needs a dedicated invariant test (for
example, N threads each issuing M commuting increments must produce exactly N*M, with
zero plain-read conflicts observed) and must be reviewed against the consistency model,
which will need a new clause describing the commute relaxation precisely.

## Suggested order

1. `retry` first, because `orElse` depends on its blocked signal and on the checkpoint
   model, and because the wait registry is the riskiest single piece and deserves to be
   isolated.
2. `orElse` second, building on the blocked signal and the buffer-truncation checkpoint.
3. `Commute` last, because it relaxes validation and is best added once the blocking
   machinery is stable and well tested.

Each step is one branch, compiled and tested locally, with its own stress test, before
the next begins.
