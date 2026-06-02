# Contributing to STMSharp

Thank you for your interest in contributing to STMSharp. This document explains how to propose changes and what to expect from the review process.

## Ways to contribute

You can contribute by reporting a bug, proposing a feature, improving the documentation, or submitting code. Bug reports and focused pull requests are the most useful contributions, because STMSharp is a concurrency library where every change to the core protocol must be justified against correctness and measurement.

## Reporting a bug

Open an issue at https://github.com/engineering87/stmsharp/issues. A useful report states what you did, what you expected to happen, and what actually happened. For a concurrency bug, include the target framework, the number of threads, and a minimal reproduction if you can produce one. Intermittent failures are worth reporting even without a reliable reproduction, because they often point at a real race; please say how often the failure occurs.

## Proposing a change

Before writing code for a non-trivial change, open an issue describing the problem you want to solve. This avoids wasted effort on a change that does not fit the design, and it gives a place to discuss the approach. The library has a declared consistency model, documented in [docs/consistency-model.md](docs/consistency-model.md), and any change to the core engine is evaluated against it.

## Development workflow

1. Fork the repository and create a branch from `main`.
2. Make your change with accompanying tests.
3. Build and run the full test suite locally before opening a pull request:

   ```bash
   dotnet build src/STMSharp/STMSharp.sln --configuration Release
   dotnet test src/STMSharp/STMSharp.sln --configuration Release
   ```

4. Open a pull request against `main` with a clear description of the change and the reasoning behind it.

## Expectations for code changes

Changes to the concurrency core are held to a high standard, because a subtle defect in an STM engine surfaces as data corruption under load rather than as an obvious crash. Two principles apply.

First, correctness is argued, not assumed. A change that touches the commit protocol, the snapshot semantics, or the locking order should explain why it preserves the guarantees in the consistency model, and it should be covered by tests that exercise the relevant concurrency, including stress tests where appropriate.

Second, performance claims are measured, not asserted. The repository includes a comparative benchmark project under `src/STMSharp.Comparative`. A change motivated by performance should be accompanied by before-and-after measurements from BenchmarkDotNet, because intuition about concurrent performance is frequently wrong. A change that does not improve a measured workload, or that improves one workload while regressing another, will be discussed on the basis of the numbers.

## Coding style

Match the style of the surrounding code. Public APIs carry XML documentation comments. Tests use xUnit. The project enables nullable reference types and treats the latest language version as available.

## License

By contributing, you agree that your contributions are licensed under the MIT License, the same license that covers the project.
