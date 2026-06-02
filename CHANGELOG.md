# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Blocking composition: `ITransaction.Retry()`. A transaction that cannot make
  progress with its current snapshot blocks on its read set and is woken when
  another transaction commits a change to one of those variables, then
  re-executes. Implemented with a lazily-allocated per-variable wait registry on
  `STMVariable<T>`, a distinct internal `TransactionBlockedException` signal, a
  recheck after registration that closes the lost-wake-up window, and a
  one-second safety-valve timeout so a missed wake degrades to a slow retry
  rather than a hang. Blocking does not consume the conflict-retry budget.
  Covered by `RetryTests`, including a one-slot producer/consumer test that
  fails by timeout if a wake-up is lost. NOTE: not yet validated by a local
  build/test run; `orElse` and commutative operations are deliberately deferred
  to follow once `retry` is validated.

### Fixed

- Corrected `BackoffPolicyTests.ExponentialWithJitter_ReturnsWithinExpectedRange`
  to match the real full-jitter contract. The delay is `Random.Shared.Next(0,
  CapExp + 1)`, an inclusive `[0, CapExp]` range, so zero is a deliberate, valid
  outcome that breaks synchronized retry storms. The test previously asserted a
  `[1, CapExp]` range and failed intermittently when the jitter returned zero. The
  production behavior was correct and is unchanged.

### Documentation

- Rewrote the README to match the current TL2 protocol and the non-generic
  `ITransaction` surface. The previous README still described the superseded
  even/odd version scheme, the generic `Transaction<T>`, `_snapshotVersions`,
  and `TryAcquireForWrite`, none of which exist in the current code.
- Added `docs/consistency-model.md`, a normative specification of the guarantees
  (atomicity, serializability, opacity, read-your-own-writes, deadlock-free
  commit), the failure and retry semantics, and the boundaries (mutable
  reference types, direct non-transactional writes, single-flow transactions,
  transactional-dictionary granularity).
- Added `docs/design-retry-orelse-commute.md`, a design note (not yet
  implemented) for blocking composition (`retry`, `orElse`) and commutative
  operations, with the suggested incremental order.
- Added the `STMSharp.Comparative` benchmark project (registered in the
  solution): a lock-based baseline and an STMSharp contended-counter
  benchmark, running the identical workload and asserting the final total so
  a lossy run fails loudly. The comparison is deliberately limited to STMSharp
  against a lock-based baseline, with no dependency on any third-party STM
  library. Not yet run; results are pending real hardware.
- Updated `docs/roadmap.md` with the findings of the first comparative run
  (single-counter contention): allocation profile and an exception-free
  budget-exhaustion path promoted to Phase 1, commutative operations tied to
  the contention case, and a disjoint-access benchmark plus an honest
  performance-claim rewrite scheduled. Lock-only, no third-party STM dependency.
- Added `DisjointAccessBenchmark` to `STMSharp.Comparative`: each thread
  operates on its own cell, contrasting a global lock that serializes
  independent work against STMSharp transactions that commit concurrently.
  It complements the single-counter worst case. Not yet run.

### Changed (BREAKING)

- The transaction core is now non-generic: a single transaction can read and
  write `STMVariable<T>` instances of different element types. The transactional
  context is exposed through the new non-generic `ITransaction`, whose `Read<T>`
  and `Write<T>` methods are generic per call. `STMEngine.Atomic` gains overloads
  that take `Action<ITransaction>` / `Func<ITransaction, ...>` and no longer
  require an element type argument.
- The legacy single-type API (`ITransaction<T>`, `STMEngine.Atomic<T>`, and
  `STMEngine.Atomic<T, TResult>`) is retained for source compatibility and now
  delegates to the non-generic core through an internal adapter. Existing code
  continues to compile and run unchanged.
- Diagnostics are now process-wide rather than per closed generic type. The
  generic `STMDiagnostics` methods are retained for source compatibility and
  ignore their type argument.
- The non-generic value-returning `Atomic<TResult>` takes an asynchronous body,
  `Func<ITransaction, Task<TResult>>`. A synchronous-result overload is not
  provided because offering both is ambiguous for any lambda that returns a
  `Task`. For a synchronous transaction that produces a value, capture the value
  with the void overload, for example `await Atomic(tx => { result = tx.Read(v); });`.
- The variable version model changed. `STMVariable<T>.Version` now reports a
  monotonic commit stamp drawn from a global version clock and is no longer
  constrained to be even; the write-lock flag is held in a separate bit and is
  not part of the reported version. `IncrementVersion()` advances the version to
  a new global stamp rather than by a fixed step.

### Added

- A TL2-style concurrency control protocol providing opacity: a running
  transaction always observes a consistent snapshot and never a torn
  intermediate state. Reads are validated against the transaction start version,
  and an inconsistent read aborts and retries the transaction.
- A global version clock (`GlobalVersionClock`) and a versioned write-lock word
  encoding (`VersionLock`) underpinning the protocol.
- Tests for heterogeneous transactions spanning multiple element types and for
  the opacity guarantee (no observable torn cross-variable state, stable
  repeated reads within a transaction).
- `TransactionalDictionary<TKey, TValue>`, a composable transactional dictionary
  with fine-grained value concurrency: each present key owns its own value cell,
  so transactions updating the values of different existing keys do not conflict.
  Membership is governed by a single structural snapshot, which validates
  observed presence or absence and prevents phantom reads. Insertion and removal
  are structural and therefore coarse, by design; a per-key structural scheme is
  intentionally deferred. Operations take an `ITransaction` and compose inside
  `STMEngine.Atomic`.

### Performance

- The transaction read set and write set are held in append-only array buffers
  with linear-scan lookup instead of dictionaries, which removes the dominant
  per-transaction allocation (the two dictionary instances and their backing
  storage) for the small transactions typical of STM. Lookups are O(n) in the
  set size; a dictionary fallback above a size threshold can be added later if
  large transactions warrant it. The read set no longer stores the observed
  version, since commit revalidates against the live version-lock word.
- Commit no longer allocates a list to track acquired write-set locks; it tracks
  the number of held locks as an index into the sorted write-set instead. This
  removes one allocation per committing read-write transaction.
- Added non-generic API benchmarks alongside the legacy ones so the baseline can
  separate the core cost from the per-call legacy adapter allocation, and a
  contended write benchmark that exercises the retry path.

### Notes

- This is the most concurrency-sensitive change in the 3.0 line. It must be
  validated by a full local build and test run, including the concurrency
  stress and opacity tests, before being trusted.

## [3.0.0-preview.1]

### Changed (BREAKING)

- `STMEngine.Atomic` now throws `TransactionConflictException` instead of
  `TimeoutException` when a transaction fails to commit after `MaxAttempts`
  due to repeated conflicts. Callers that catch `TimeoutException` must be
  updated. The dedicated exception type was previously defined but unused.

### Added

- Multi-targeting for `net8.0` (LTS) and `net10.0` (current). This is the
  first step toward broad ecosystem reach.
- Continuous integration workflow (GitHub Actions) that builds and tests the
  solution across the supported target frameworks and produces NuGet packages.
- Source Link, deterministic builds, embedded untracked sources, and symbol
  packages (`snupkg`) for debuggable, verifiable packages.
- XML documentation file is now generated and shipped with the package.
- Concurrency stress test that validates the increment-sum invariant under
  high contention as a serializability smoke test.

### Fixed

- Corrected the diagnostics class name in the README from `StmDiagnostics`
  to `STMDiagnostics` so that the documented examples compile.
- Removed an empty, stray `Program.cs` placeholder under
  `src/STMSharp/STMSharp.Benchmarking` that was unreferenced by the solution.
- Renamed `StmDiagnostics.cs` to `STMDiagnostics.cs` so that the file name
  matches the contained type.

### Notes

- `netstandard2.0` and .NET Framework support is planned for a subsequent
  preview and requires a dedicated compatibility layer that polyfills the
  modern BCL helpers in use (`Random.Shared`, `Math.Clamp`,
  `ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException` guards,
  and `ReferenceEqualityComparer`).
