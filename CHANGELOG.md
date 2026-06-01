# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
