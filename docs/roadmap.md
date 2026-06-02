# STMSharp Roadmap

This roadmap describes the path for STMSharp to become the reference Software
Transactional Memory library for .NET. It is organized around the qualities that decide
whether a library is adopted as a standard: a declared and verified consistency model,
competitive and reproducible performance, an API that makes correct use the easy path,
and the ecosystem trust that comes from documentation and disciplined versioning.

Two constraints frame the whole roadmap. First, every change to the concurrency core
must be compiled and tested locally, one increment at a time, before it is trusted; a
concurrency defect that is neither compiled nor tested is the worst outcome for a library
of this kind. Second, comparative benchmarking is deliberately limited to a lock-based
baseline. STMSharp does not take a dependency on any third-party STM library, in its
product code or in its benchmarks. The lock-based baseline is the reference every .NET
developer already knows, and it is the honest yardstick for contention behavior.

## Current state

The core is mature. The TL2-style protocol with opacity, heterogeneous transactions over
a non-generic transaction context, the deterministic deadlock-free commit, the two-phase
backoff that keeps contended transactions on the microsecond scale, and the transactional
dictionary with phantom prevention are all implemented and covered by a serious test
suite. What remains is composition, a leaner allocation profile, an honest performance
story, and the ecosystem layer.

## What the first comparative run established

The single-counter contended benchmark (STMSharp against a lock-based baseline,
4 and 16 threads, 1000 increments each) produced clear, expected results that shape the
priorities below.

- On a single maximally contended variable the lock baseline wins: STMSharp was about
  2.45x slower at 4 threads (638 us against 260 us) and about 2.00x slower at 16 threads
  (2.51 ms against 1.26 ms). This is the worst case for any optimistic STM, because a
  single shared counter has no disjoint work to parallelize, so the STM pays its overhead
  without being able to collect its only advantage. The result is not a design defect; it
  is the expected behavior of optimistic concurrency under total contention.
- Allocation is the real problem. STMSharp allocated on the order of a few hundred bytes
  per attempted transaction, which at 16 threads drove about 453 Gen0 collections per
  1000 operations. This is the dominant, addressable cost.
- Conflict-exhaustion exceptions are used as control flow. The run recorded about 15.5
  exceptions per operation at 4 threads and about 229 at 16, that is roughly 1.4 percent
  of transactions exhausting the 64-attempt budget at 16 threads, each paying a stack
  unwind. Using exceptions for the normal contended path is expensive.

These three findings promote allocation work and the exception-free path from "future
optimization" to concrete, prioritized tasks, and they settle the performance-claim
question: on single-variable contention STMSharp will not beat a lock, so the README must
not claim otherwise.

## Completed

- TL2 protocol with opacity, serializability, read-your-own-writes, deadlock-free commit.
- Non-generic `ITransaction` with heterogeneous per-call reads and writes; legacy
  single-type surface retained via an adapter.
- Two-phase backoff (sub-millisecond spin, then a timed ladder) that collapsed contended
  write latency from the millisecond scale to the microsecond scale.
- `TransactionConflictException` after budget exhaustion (breaking change).
- `TransactionalDictionary` with per-key value concurrency and structural phantom
  prevention.
- README realigned to the actual protocol.
- `docs/consistency-model.md`, the normative specification of the guarantees.
- `STMSharp.Comparative` benchmark project: STMSharp against a lock-based baseline, no
  third-party STM dependency. First run completed (single-counter contention).

## Phase 1: Allocation profile and the exception-free path

Promoted to first because all three comparative runs identify allocation as the dominant
cost (about a 2000x allocation ratio versus a lock, paid with or without contention), and
because it does not change the consistency model, so it is low-risk to land early. This is
now the single most valuable remaining piece of technical work.

- Reduce per-attempt allocation. Eliminate boxing in the variable storage, and reuse or
  pool the read and write buffers across retries of the same transaction so that a
  contended transaction that retries many times does not allocate many times. Target the
  several-hundred-bytes-per-attempt figure measured in the run and confirm the reduction
  against the lock baseline before and after.
- Exception-free budget-exhaustion path: IMPLEMENTED (pending local build/test validation).
  `TryAtomic` returns the outcome (`Task<bool>` for void forms, `Task<(bool Committed,
  TResult Value)>` for the value form) instead of throwing `TransactionConflictException`,
  so the contended retry loop does not pay a stack unwind. The throwing `Atomic` stays and
  now shares a single non-throwing core. The consistency model describes both outcomes.
  Covered by `TryAtomicTests`. Allocation reduction (below) is the remaining Phase 1 work.

Each change is measured with the comparative benchmark, before and after, on the same
machine.

## Phase 2: Blocking composition (retry / orElse)

The composition layer is what separates a mature STM from an atomic-commit mechanism. The
design is captured in `docs/design-retry-orelse-commute.md`.

- `retry`: IMPLEMENTED (pending local build/test validation). A transaction declares it
  cannot proceed and blocks until a variable in its read set changes, instead of spinning
  or returning a sentinel. Built with a lazily-allocated per-variable wait registry, an
  after-registration recheck that closes the lost-wake-up window, and a one-second
  safety-valve timeout. Covered by `RetryTests`, including a one-slot producer/consumer
  test that fails by timeout on a lost wake-up. Must be compiled and tested locally before
  it is trusted.
- `orElse`: IMPLEMENTED (pending local build/test validation). Composes two alternatives,
  trying the second only if the first blocks, using the write-set count as a cheap
  checkpoint to roll back the first alternative's tentative writes while retaining its
  reads, so a both-block case blocks on the union of read sets. Covered by `OrElseTests`.
  Must be compiled and tested locally before it is trusted.

Each lands on its own branch, compiled and tested, before the next begins. This phase
extends the consistency model document with the blocking semantics.

## Phase 3: Commutative operations

Commutative operations let independent updates that commute, such as counter increments,
avoid conflicting with one another. IMPLEMENTED (pending local build/test validation):
`ITransaction.Commute(variable, operation)` buffers a function applied to the live committed
value under lock at commit, so two commuting updates to the same variable do not conflict.
This directly addresses the single-counter contention result. A variable also touched
non-commutatively in the same transaction falls back to the validated path (commute set and
write set kept disjoint), and the read-set validation skip is disabled when a commute is
present. Covered by `CommuteTests`, including a 16x1000 conservation invariant, and the
consistency model gains a commute clause. Must be compiled and tested locally before it is
trusted, with particular attention to the conservation invariant under contention.

## Phase 4: Honest, complete benchmarking and documentation

- Disjoint-access benchmark: ADDED (`DisjointAccessBenchmark`). Each thread operates on
  its own cell, so STM transactions can proceed in parallel while a single global lock
  serializes them. Pairing it with the single-counter worst case is what makes the
  comparison credible. A future benchmark should add a fine-grained per-cell lock and a
  dynamic, data-dependent access pattern, since a per-cell lock is competitive on
  statically disjoint access and STMSharp earns its keep on dynamic patterns and
  multi-variable atomicity. Run pending.
- Re-run the full comparative suite after each of Phases 1 to 3 and record the numbers.
  `ContendedCounterBenchmark` measures the contended counter three ways (lock,
  STMSharp read-modify-write, STMSharp `Commute`). Run completed: on a single
  contended counter the lock is fastest; STMSharp read-modify-write is about two to
  three times slower; and `Commute` is the slowest of the three (about eleven times
  the lock at four threads and over thirty times at sixteen), because the ordered
  spin-wait that fixed the commute livelock turns heavy single-variable contention
  into busy-waiting. `Commute` is correct (the conservation invariant holds) but its
  value is on commutative updates distributed across many variables, not on one hot
  point. No STM variant beats a lock on a small contended section; this is settled by
  three independent runs and is reflected in the README.
- README performance section: REWRITTEN to the truth the data supports. A lock is faster
  on a small contended section; STMSharp earns its place on composable multi-variable
  atomicity, blocking composition, and a verified consistency model, not on raw speed. The
  unqualified superiority claim is removed.

## Phase 5: Governance and versioning

Apply SemVer with discipline, document the breaking-change policy, define the stable
public surface, and stabilize the `3.0.0-preview` line to a final `3.0.0` once Phases 1
to 3 have landed. For a library intended as a standard, the predictability of versions
matters as much as the code.

## Explicitly out of scope

- Dependency on, or comparison against, any third-party STM library, in product code or
  benchmarks. The comparison is STMSharp against locks.
- `netstandard2.0` and .NET Framework reach. This is a question of runtime coverage, not
  of maturity, and is not pursued.

## Suggested order

Phase 1 first, because the comparative run identified allocation and the exception path as
the dominant, low-risk costs. Phases 2 and 3 add the composition that defines a mature
STM, with commute (Phase 3) directly addressing the contention case. Phase 4 keeps the
performance story honest at every step, and Phase 5 closes the loop on trust.
