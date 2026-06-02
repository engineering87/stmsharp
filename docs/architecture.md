# STMSharp Architecture

This document describes the internal design and the TL2-style algorithm that STMSharp implements. It is intended for contributors and for readers who want to understand the engine beyond the usage surface. The normative guarantees are specified separately in [consistency-model.md](consistency-model.md); this document explains the mechanism that provides them.

## Design goals

STMSharp is built around four goals, in priority order. Correctness of the consistency model comes first: the engine must provide serializability and opacity, and it must never publish a partial or inconsistent result. Composability comes second: transactions must combine without the deadlock hazards of hand-ordered locks, including blocking coordination. Predictability comes third: behavior under contention must be understandable and free of unbounded pathologies such as livelock. Raw single-point throughput comes last, and is explicitly not a goal where a single lock would serve better.

## The versioned write-lock word

Every `STMVariable<T>` stores its value alongside a single 64-bit word that encodes both a lock flag and a version. Bit 0 is the lock flag, and the remaining 63 bits hold the version. Packing both into one word lets a reader observe the lock state and the version in a single atomic read, with no possibility of seeing a version that belongs to a different lock state. Versions are not per-variable counters; they are stamps drawn from a single process-wide `GlobalVersionClock`, which means any two versions across any two variables are directly comparable. This global comparability is what makes snapshot validation possible: a transaction can decide whether a variable has changed since the transaction started simply by comparing version stamps.

## Transaction-local buffers

A transaction does not mutate shared state as it runs. It accumulates its effects in three append-only buffers held in the transaction object. The read set records the distinct variables the transaction has read. The write set records variables together with their pending values, which are published only at commit. The commute set records variables with a pending commutative operation, applied at commit time rather than as an immediate read-modify-write.

These buffers are plain arrays scanned linearly, not hash tables. For the small transactions typical of STM, a linear scan over a handful of entries is faster and allocates far less than hashing, and it avoids the per-entry overhead that would dominate the cost of a short transaction.

## The read path and opacity

When a transaction reads a variable, it first checks its own write set, so that a value it has already written in the same transaction is returned to it (read-your-own-writes). Otherwise it reads the variable's value and version-lock word. If the variable is locked by another committer, or its version is newer than the transaction's start version, the snapshot the transaction is building is no longer consistent. Rather than continue on stale data, the read raises an internal retry signal that unwinds the user delegate immediately, and the engine retries the whole transaction from a fresh start version.

This immediate unwind is what provides opacity. A transaction that is doomed to abort never proceeds to execute application logic on an inconsistent snapshot, so it cannot be driven into an exception, an infinite loop, or any other undefined behavior by a concurrent commit. Opacity is a stronger property than serializability alone, and it is the property that makes STM safe to use with ordinary, non-defensive application code inside the transaction.

## The commit protocol

A read-only transaction, or a read-write transaction whose write set turned out to be empty, has nothing to publish. Every read it performed was validated against the start version as it happened, so it commits with no further work.

A read-write transaction commits in four steps. First it locks the union of its write set and commute set, in a deterministic total order defined by a stable per-variable identifier, acquiring each lock with a compare-and-swap. Second it advances the global clock once to obtain its write version. Third it revalidates its read set against the live version-lock words: if any read variable is now locked by another committer or carries a version newer than the start version, the transaction releases the locks it holds and aborts to retry. Fourth, having confirmed its snapshot is still valid, it publishes the buffered write values and applies the commute operations, then releases each lock while stamping the new write version.

There is one optimization in the third step. If the write version is exactly one greater than the start version, then no other transaction can have committed in the interval between this transaction's start and its commit, so the read set cannot have changed and the revalidation is skipped. This optimization is disabled whenever the commute set is non-empty, because a commute advances a variable's version without that variable appearing in the read set, so the clock delta alone can no longer rule out an intervening commit.

## Deadlock freedom

Locking several variables at commit raises the classic deadlock question. STMSharp answers it with a total order. Every variable has a stable identifier, and the commit protocol always acquires locks in increasing identifier order. Two committers can therefore never hold-and-wait in a cycle, because a cycle would require one committer to hold a higher-identifier lock while waiting for a lower-identifier one, which the ordering forbids. The committer holding the lowest-identifier locks in any contended set is always able to make progress, which guarantees global progress and rules out deadlock.

The same total order is what makes it safe for the lock acquisition to wait on contention rather than abort. An earlier version aborted a transaction that lost a race for a commit lock, which under heavy single-variable contention could turn into a livelock where an unlucky transaction lost the race repeatedly until it exhausted its retry budget. Because the locks are taken in a total order, waiting for a contended lock is deadlock-free, so the acquisition now waits rather than aborts. This removes the spurious aborts that pure physical lock contention used to cause; only a genuine read-set conflict aborts a transaction.

## Composable blocking: retry and orElse

`Retry` expresses a transaction that cannot proceed until some condition holds. When the delegate calls `Retry`, the engine does not consume the conflict budget, because the transaction is waiting on a condition rather than losing a race. Instead it registers a waiter on each variable in the transaction's read set and parks until one of those variables is committed by another transaction, at which point it re-executes. A safety-valve timeout bounds the wait so a missed wake-up cannot hang a transaction permanently. A lost-wake-up window is closed by rechecking the read set after registering the waiter, so a relevant commit that landed between the `Retry` call and the registration is observed immediately rather than waited on.

`OrElse` composes two blocking transactions into one. It runs the first; if the first blocks via `Retry`, `OrElse` discards the first transaction's writes, keeps its reads, and runs the second alternative. If the second also blocks, the combined transaction blocks on the union of both read sets, so a change to any variable that either branch depends on wakes it. This mirrors the semantics of Composable Memory Transactions.

## Commutative updates

`Commute` exists for the common case of an update that commutes with itself, such as an increment or an addition to a set. Instead of reading the variable, computing a new value, and writing it back, which makes the transaction conflict with every other read-modify-write of the same variable, `Commute` registers the operation and defers it. At commit, under the variable's lock, the operation is applied to the variable's live committed value. Two transactions that only commute the same variable therefore do not conflict logically: each applies its operation to whatever the committed value is at commit time, and both can succeed.

Commute is not a performance win on a single contended variable, because every commit must still serialize on that variable's lock, and the conservation of the result is paid for with that serialization. Its value is on commutative updates distributed across many variables, where the absence of logical conflict lets independent transactions proceed in parallel. The disjointness between the commute set and the write set is maintained by the engine: a variable touched both ways in one transaction is materialized into the validated write path, so the two sets never overlap.

## Diagnostics

`STMDiagnostics` exposes process-wide counters for conflicts, retries, and unresolved conflicts (transactions that exhausted their budget). These are intended for observability and for understanding contention in a workload, not as a part of the transactional semantics. They are updated with interlocked operations and can be reset.

## What is deliberately not optimized

The per-transaction allocation profile is left as measured rather than aggressively reduced. An allocation profile attributed most of the per-transaction cost to the transaction object itself, with value boxing a small fraction and buffers already lazily allocated. A transaction-instance-reuse optimization was implemented and measured, but it did not improve the contended benchmarks and it touched the snapshot baseline on the core path, so it was reverted. The reasoning and the measurements are recorded in the [roadmap](roadmap.md). The lesson encoded here is that changes to the concurrency core are made against measurement, not intuition.
