# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
