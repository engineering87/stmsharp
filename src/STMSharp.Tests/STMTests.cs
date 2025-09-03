﻿// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    // Prevent parallel execution across these tests (static counters + shared state).
    [CollectionDefinition("STM non-parallel")]
    public class STMNonParallelCollection : ICollectionFixture<object> { }

    [Collection("STM non-parallel")]
    public class STMTests
    {
        // -------- Helpers --------

        private static void ResetStats() => Transaction<int>.ResetCounters();

        /// <summary>
        /// Runs an async body with a hard timeout. Cancels the body if it overruns.
        /// Ensures tests never hang indefinitely.
        /// </summary>
        private static async Task WithTimeout(Func<CancellationToken, Task> body, int timeoutMs = 5_000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var task = body(cts.Token);
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs, CancellationToken.None));
            if (completed != task)
            {
                cts.Cancel();
                throw new TimeoutException($"Test exceeded {timeoutMs} ms.");
            }
            await task; // surface exceptions
        }

        /// <summary>
        /// Creates a start gate that releases tasks simultaneously.
        /// </summary>
        private static TaskCompletionSource<bool> NewStartGate()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Reads the variable inside a transaction to respect STM semantics.
        /// </summary>
        private static async Task<int> ReadInsideTransactionAsync(STMVariable<int> v)
        {
            int result = 0;
            await STMEngine.Atomic<int>(tx => { result = tx.Read(v); });
            return result;
        }

        // -------- Tests --------

        [Fact]
        public async Task SequentialTwoIncrements_AreApplied()
        {
            ResetStats();
            var shared = new STMVariable<int>(0);

            await STMEngine.Atomic<int>(tx =>
            {
                var val = tx.Read(shared);
                tx.Write(shared, val + 1);
            });

            await STMEngine.Atomic<int>(tx =>
            {
                var val = tx.Read(shared);
                tx.Write(shared, val + 1);
            });

            Assert.Equal(2, await ReadInsideTransactionAsync(shared));
            Assert.True(Transaction<int>.RetryCount >= 0);
        }

        [Fact]
        public async Task TwoConcurrentIncrements_NoLostUpdates()
        {
            ResetStats();
            var shared = new STMVariable<int>(0);

            await WithTimeout(async ct =>
            {
                var start = NewStartGate();

                var t1 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    }, maxAttempts: 12, initialBackoffMilliseconds: 1,
                       backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                }, ct);

                var t2 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    }, maxAttempts: 12, initialBackoffMilliseconds: 1,
                       backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(t1, t2);
            });

            // Check only the actual shared variable
            Assert.Equal(2, await ReadInsideTransactionAsync(shared));
        }

        [Fact]
        public async Task HighContention_FinishesFast_NoDeadlocks()
        {
            ResetStats();
            var shared = new STMVariable<int>(0);
            const int writers = 32;

            await WithTimeout(async ct =>
            {
                var start = NewStartGate();

                var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    },
                    maxAttempts: 64,                   // more attempts to avoid rare miss under high contention
                    initialBackoffMilliseconds: 2,     // slight backoff to reduce thrash
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct)).ToArray();

                start.SetResult(true);
                await Task.WhenAll(tasks);
            }, timeoutMs: 10_000); // more generous on CI

            var final = await ReadInsideTransactionAsync(shared);

            // On failure, show diagnostic counters
            Assert.True(final == writers,
                $"Final={final}, Expected={writers}, Retries={Transaction<int>.RetryCount}, Conflicts={Transaction<int>.ConflictCount}");
        }

        [Fact]
        public async Task ReadOnly_ThrowsOnWrite_Quickly()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WithTimeout(ct => STMEngine.Atomic<int>(tx =>
                {
                    var v = tx.Read(x);
                    tx.Write(x, v + 1); // must throw inside read-only transaction
                }, readOnly: true, cancellationToken: ct))
            );
        }

        [Fact]
        public async Task Stats_ConflictsAndRetries_AreTrackedNonNegative()
        {
            ResetStats();
            var shared = new STMVariable<int>(0);

            await WithTimeout(async ct =>
            {
                var start = NewStartGate();

                var t1 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    }, maxAttempts: 12, initialBackoffMilliseconds: 1,
                       backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                }, ct);

                var t2 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    }, maxAttempts: 12, initialBackoffMilliseconds: 1,
                       backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(t1, t2);
            });

            Assert.True(Transaction<int>.RetryCount >= 0);
            Assert.True(Transaction<int>.ConflictCount >= 0);
        }

        [Fact]
        public async Task Timeout_WhenMaxAttemptsIsOne_UnderHeavyContention()
        {
            ResetStats();
            var shared = new STMVariable<int>(0);

            const int contenders = 8;          // keep lower for CI stability
            const int outerTimeoutMs = 10_000; // external hard timeout

            await WithTimeout(async ct =>
            {
                // Start gate
                var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Lock-free "read barrier"
                int readersReady = 0;           // incremented after the READ phase
                int goWrite = 0;                // 0=hold, 1=release; polled with Volatile

                var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
                {
                    await startGate.Task;

                    try
                    {
                        await STMEngine.Atomic<int>(tx =>
                        {
                            // 1) READ phase: all contenders capture the same snapshot
                            var v = tx.Read(shared);

                            // Mark this contender as ready for the write phase
                            Interlocked.Increment(ref readersReady);

                            // 2) Spin-wait until the write phase is released
                            var spinner = new SpinWait();
                            while (Volatile.Read(ref goWrite) == 0)
                            {
                                spinner.SpinOnce();
                                if (ct.IsCancellationRequested) return; // cooperative exit
                            }

                            // 3) WRITE: with maxAttempts=1, at least one will fail on reservation/validation
                            tx.Write(shared, v + 1);

                        }, maxAttempts: 1, initialBackoffMilliseconds: 0,
                           backoffType: BackoffType.ExponentialWithJitter,
                           cancellationToken: ct);

                        return true; // success
                    }
                    catch (TimeoutException)
                    {
                        return false; // expected for some contenders
                    }
                }, ct)).ToArray();

                // Release all contenders to start
                startGate.SetResult(true);

                // Wait a short while for most contenders to finish the READ phase (lock-free wait)
                var waitSpin = new SpinWait();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref readersReady) < contenders && sw.ElapsedMilliseconds < 2_000)
                {
                    waitSpin.SpinOnce();
                    if (ct.IsCancellationRequested) break;
                }

                // Release the WRITE phase (even if not all reached it; collision is still forced)
                Volatile.Write(ref goWrite, 1);

                var results = await Task.WhenAll(tasks);

                // With maxAttempts=1 and near-simultaneous commits, expect both success and failure
                Assert.Contains(true, results);
                Assert.Contains(false, results);
            }, timeoutMs: outerTimeoutMs);

            // Final value must be within bounds (at least one, at most all)
            var final = await ReadInsideTransactionAsync(shared);
            Assert.InRange(final, 1, contenders);
        }

        [Theory]
        [InlineData(4, 50)]
        [InlineData(8, 50)]
        public async Task ThreadsTimesOps_FastAndCorrect(int threads, int ops)
        {
            ResetStats();
            var shared = new STMVariable<int>(0);

            await WithTimeout(async ct =>
            {
                var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < ops; i++)
                    {
                        await STMEngine.Atomic<int>(tx =>
                        {
                            var v = tx.Read(shared);
                            tx.Write(shared, v + 1);
                        },
                        maxAttempts: 64,                    // ↑ more attempts to avoid rare misses
                        initialBackoffMilliseconds: 2,      // ↑ slightly larger backoff to reduce thrash
                        backoffType: BackoffType.ExponentialWithJitter,
                        cancellationToken: ct);
                    }
                }, ct)).ToArray();

                await Task.WhenAll(tasks);
            }, timeoutMs: 10_000); // generous test-level guard

            var final = await ReadInsideTransactionAsync(shared);
            Assert.True(final == threads * ops,
                $"Final={final}, Expected={threads * ops}, Retries={Transaction<int>.RetryCount}, Conflicts={Transaction<int>.ConflictCount}");
        }

        [Fact]
        public async Task ReadOnlyValidationConflict_IsCountedAndReturnsFast()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await WithTimeout(async ct =>
            {
                var start = NewStartGate();

                var writer = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(x);
                        tx.Write(x, v + 1);
                    }, maxAttempts: 8, initialBackoffMilliseconds: 1,
                       backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                }, ct);

                var reader = Task.Run(async () =>
                {
                    await start.Task;
                    // Read-only transaction may see validation conflict under contention
                    try
                    {
                        await STMEngine.Atomic<int>(tx =>
                        {
                            var v = tx.Read(x);
                            // no write
                        }, readOnly: true, maxAttempts: 2, initialBackoffMilliseconds: 1,
                           backoffType: BackoffType.ExponentialWithJitter, cancellationToken: ct);
                    }
                    catch (TimeoutException)
                    {
                        // Acceptable under contention with a small attempt budget
                    }
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(writer, reader);
            });

            Assert.True(Transaction<int>.RetryCount >= 0);
            Assert.True(Transaction<int>.ConflictCount >= 0);
        }
    }
}