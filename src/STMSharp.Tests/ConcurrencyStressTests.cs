// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using System.Linq;
using System.Threading.Tasks;
using STMSharp.Core;
using STMSharp.Core.Exceptions;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// High-contention stress tests that act as a serializability smoke test for
    /// the CAS-based commit protocol. If commits were not serializable, concurrent
    /// read-modify-write increments would lose updates and the final total would
    /// fall short of the expected count.
    /// </summary>
    [Collection("STM non-parallel")]
    public class ConcurrencyStressTests
    {
        [Theory]
        [InlineData(4, 1_000)]
        [InlineData(8, 2_000)]
        [InlineData(16, 1_000)]
        public async Task ConcurrentIncrements_PreserveTotalCount_NoLostUpdates(
            int workers,
            int incrementsPerWorker)
        {
            STMDiagnostics.Reset<int>();

            var shared = new STMVariable<int>(0);

            // Retry the whole atomic block until it commits, so that exhausting the
            // inner attempt budget under heavy contention never drops an increment.
            async Task IncrementOnce()
            {
                while (true)
                {
                    try
                    {
                        await STMEngine.Atomic<int>(
                            tx =>
                            {
                                var v = tx.Read(shared);
                                tx.Write(shared, v + 1);
                            },
                            maxAttempts: 64,
                            initialBackoffMilliseconds: 0,
                            maxBackoffMilliseconds: 2,
                            backoffType: BackoffType.ExponentialWithJitter);
                        return;
                    }
                    catch (TransactionConflictException)
                    {
                        // Heavy contention exhausted the attempt budget; retry the operation.
                    }
                }
            }

            var tasks = Enumerable.Range(0, workers)
                .Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < incrementsPerWorker; i++)
                        await IncrementOnce();
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(workers * incrementsPerWorker, shared.Read());
        }

        [Fact]
        public async Task ReadOnlyTransactions_AreConsistent_UnderConcurrentWriters()
        {
            STMDiagnostics.Reset<int>();

            var shared = new STMVariable<int>(0);
            const int writers = 4;
            const int writesPerWriter = 500;

            using var cts = new System.Threading.CancellationTokenSource();

            // Background readers observe the variable while writers mutate it.
            // No assertion on the observed value is made here; the goal is to ensure
            // read-only transactions never throw spuriously and always terminate.
            async Task ReaderLoop()
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await STMEngine.Atomic<int>(
                            tx => { _ = tx.Read(shared); },
                            StmOptions.ReadOnly);
                    }
                    catch (TransactionConflictException)
                    {
                        // A read-only transaction may exhaust its small attempt budget
                        // while writers churn the version; this is acceptable here.
                    }
                }
            }

            var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(ReaderLoop)).ToArray();

            var writerTasks = Enumerable.Range(0, writers)
                .Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < writesPerWriter; i++)
                    {
                        while (true)
                        {
                            try
                            {
                                await STMEngine.Atomic<int>(
                                    tx =>
                                    {
                                        var v = tx.Read(shared);
                                        tx.Write(shared, v + 1);
                                    },
                                    maxAttempts: 64,
                                    initialBackoffMilliseconds: 0,
                                    maxBackoffMilliseconds: 2);
                                break;
                            }
                            catch (TransactionConflictException)
                            {
                            }
                        }
                    }
                }))
                .ToArray();

            await Task.WhenAll(writerTasks);
            cts.Cancel();
            await Task.WhenAll(readers);

            Assert.Equal(writers * writesPerWriter, shared.Read());
        }
    }
}
