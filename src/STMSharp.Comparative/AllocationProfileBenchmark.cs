// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using BenchmarkDotNet.Attributes;
using STMSharp.Core;

namespace STMSharp.Comparative
{
    /// <summary>
    /// Isolates the sources of per-transaction allocation, single-threaded and uncontended
    /// so that retries do not confuse the picture (with no contention every transaction
    /// commits on its first attempt, so the measured allocation is the cost of exactly one
    /// successful transaction).
    ///
    /// Read the three results together:
    ///   - ValueTypeWrite   : one Write of an int. Allocates the transaction object, its
    ///                        buffers, the lock plan, AND boxes the int value.
    ///   - ReferenceTypeWrite: one Write of a string (already a reference, so no value
    ///                        boxing). Allocates the transaction object, its buffers, and
    ///                        the lock plan, but NOT a box for the value.
    ///   - EmptyReadOnly    : a read-only transaction that only reads one variable. Allocates
    ///                        the transaction object and the read buffer, no write/commute
    ///                        buffers and no lock plan.
    ///
    /// Therefore:
    ///   boxing cost per write    ~= ValueTypeWrite - ReferenceTypeWrite
    ///   write-path buffer + plan ~= ReferenceTypeWrite - EmptyReadOnly
    ///   base transaction + read  ~= EmptyReadOnly
    ///
    /// This is what tells us, with data rather than intuition, whether to attack boxing,
    /// the transaction lifecycle, or neither.
    /// </summary>
    [MemoryDiagnoser]
    public class AllocationProfileBenchmark
    {
        private readonly STMVariable<int> _intVar = new(0);
        private readonly STMVariable<string> _strVar = new("seed");
        private static readonly string[] Payloads = { "a", "b", "c", "d" };

        [Benchmark(Baseline = true)]
        public void ValueTypeWrite()
        {
            // One uncontended read-modify-write of an int. The int written is a value type,
            // so publishing it into the variable's object-typed storage boxes it.
            STMEngine.Atomic(tx =>
            {
                int v = tx.Read(_intVar);
                tx.Write(_intVar, v + 1);
            }).GetAwaiter().GetResult();
        }

        [Benchmark]
        public void ReferenceTypeWrite()
        {
            // One uncontended read-modify-write of a string. A string is already a reference,
            // so no value box is allocated when it is published; the difference from
            // ValueTypeWrite is the boxing cost.
            STMEngine.Atomic(tx =>
            {
                string s = tx.Read(_strVar);
                int next = (s.Length + 1) & 3;
                tx.Write(_strVar, Payloads[next]);
            }).GetAwaiter().GetResult();
        }

        [Benchmark]
        public void EmptyReadOnly()
        {
            // A read-only transaction that only reads. No write or commute buffers, no lock
            // plan: this isolates the base cost of the transaction object plus the read set.
            STMEngine.Atomic(tx =>
            {
                _ = tx.Read(_intVar);
            }, readOnly: true).GetAwaiter().GetResult();
        }
    }
}
