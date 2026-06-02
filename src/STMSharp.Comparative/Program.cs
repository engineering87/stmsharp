// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Running;

namespace STMSharp.Comparative
{
    // Comparative benchmark harness: STMSharp vs a lock-based baseline.
    //
    // Two complementary workloads bracket the spectrum honestly:
    //   - ContendedCounterBenchmark: one shared counter, the worst case for an optimistic
    //     STM, where a lock wins.
    //   - DisjointAccessBenchmark: each thread on its own cell, where a global lock
    //     needlessly serializes independent work and an STM can proceed in parallel.
    //
    // No third-party STM is referenced; the comparison is deliberately limited to STMSharp
    // and locks.
    //
    // IMPORTANT: this project has NOT been compiled in the authoring environment. Run a
    // local `dotnet build` and a short `dotnet run -c Release` before trusting numbers.
    internal static class Program
    {
        private static void Main(string[] args)
            => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
