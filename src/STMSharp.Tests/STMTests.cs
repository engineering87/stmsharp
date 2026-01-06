// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    /// <summary>
    /// Prevent parallel execution across these tests (shared STM stats + shared variables).
    /// </summary>
    [CollectionDefinition("STM non-parallel")]
    public class STMNonParallelCollection : ICollectionFixture<object> { }

    [Collection("STM non-parallel")]
    public class STMTests
    {
        // -------- Helpers --------

        /// <summary>
        /// Resets static STM stats (per closed generic type int).
        /// </summary>
        private static void ResetStats() => STMDiagnostics.Reset<int>();

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

            // Propagate any exception from the body
            await task;
        }

        /// <summary>
        /// Creates a start gate that releases tasks simultaneously.
        /// </summary>
        private static TaskCompletionSource<bool> NewStartGate()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Reads the variable inside a transaction to respect STM semantics
        /// (i.e., uses the same snapshot/validation pipeline).
        /// </summary>
        private static async Task<int> ReadInsideTransactionAsync(STMVariable<int> v)
        {
            int result = 0;

            await STMEngine.Atomic<int>(tx =>
            {
                result = tx.Read(v);
            });

            return result;
        }

        // -------- Core concurrency tests --------

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
            Assert.True(STMDiagnostics.GetRetryCount<int>() >= 0);
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
                    },
                    maxAttempts: 12,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct);

                var t2 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    },
                    maxAttempts: 12,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
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
                    maxAttempts: 64,                  // more attempts to avoid rare miss under high contention
                    initialBackoffMilliseconds: 2,    // slight backoff to reduce thrash
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct)).ToArray();

                start.SetResult(true);
                await Task.WhenAll(tasks);
            }, timeoutMs: 10_000); // more generous on CI

            var final = await ReadInsideTransactionAsync(shared);

            // On failure, show diagnostic counters
            Assert.True(final == writers,
                $"Final={final}, Expected={writers}, Retries={STMDiagnostics.GetRetryCount<int>()}, Conflicts={STMDiagnostics.GetConflictCount<int>()}");
        }

        [Fact]
        public async Task ReadOnly_ThrowsOnWrite_Quickly()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WithTimeout(ct =>
                    STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(x);
                        tx.Write(x, v + 1); // must throw inside read-only transaction
                    },
                    readOnly: true,
                    cancellationToken: ct))
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
                    },
                    maxAttempts: 12,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct);

                var t2 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(shared);
                        tx.Write(shared, v + 1);
                    },
                    maxAttempts: 12,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(t1, t2);
            });

            Assert.True(STMDiagnostics.GetRetryCount<int>() >= 0);
            Assert.True(STMDiagnostics.GetConflictCount<int>() >= 0);
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

                        },
                        maxAttempts: 1,
                        initialBackoffMilliseconds: 0,
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
                        maxAttempts: 64,                   // ↑ more attempts to avoid rare misses
                        initialBackoffMilliseconds: 2,     // ↑ slightly larger backoff to reduce thrash
                        backoffType: BackoffType.ExponentialWithJitter,
                        cancellationToken: ct);
                    }
                }, ct)).ToArray();

                await Task.WhenAll(tasks);
            }, timeoutMs: 10_000); // generous test-level guard

            var final = await ReadInsideTransactionAsync(shared);
            Assert.True(final == threads * ops,
                $"Final={final}, Expected={threads * ops}, Retries={STMDiagnostics.GetRetryCount<int>()}, Conflicts={STMDiagnostics.GetConflictCount<int>()}");
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
                    },
                    maxAttempts: 8,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
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
                        },
                        readOnly: true,
                        maxAttempts: 2,
                        initialBackoffMilliseconds: 1,
                        backoffType: BackoffType.ExponentialWithJitter,
                        cancellationToken: ct);
                    }
                    catch (TimeoutException)
                    {
                        // Acceptable under contention with a small attempt budget
                    }
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(writer, reader);
            });

            Assert.True(STMDiagnostics.GetRetryCount<int>() >= 0);
            Assert.True(STMDiagnostics.GetConflictCount<int>() >= 0);
        }

        // -------- Extra behavioral tests --------

        [Fact]
        public async Task ReadOnly_DoesNotChangeValue()
        {
            ResetStats();
            var x = new STMVariable<int>(42);

            await STMEngine.Atomic<int>(tx =>
            {
                var v = tx.Read(x);
                // even if we do some computation, we never write
                var _ = v + 10;
            },
            readOnly: true);

            var final = await ReadInsideTransactionAsync(x);
            Assert.Equal(42, final);
        }

        [Fact]
        public async Task WriteFirstWithoutRead_CommitsCorrectly()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            await STMEngine.Atomic<int>(tx =>
            {
                // First observation is a Write: must still capture a valid snapshot
                tx.Write(x, 123);
            });

            var final = await ReadInsideTransactionAsync(x);
            Assert.Equal(123, final);
        }

        [Fact]
        public async Task MultiVariableTransaction_IsAtomic()
        {
            ResetStats();
            var a = new STMVariable<int>(1);
            var b = new STMVariable<int>(2);

            await STMEngine.Atomic<int>(tx =>
            {
                var va = tx.Read(a);
                var vb = tx.Read(b);

                tx.Write(a, va + 10); // 11
                tx.Write(b, vb + 20); // 22
            });

            var finalA = await ReadInsideTransactionAsync(a);
            var finalB = await ReadInsideTransactionAsync(b);

            Assert.Equal(11, finalA);
            Assert.Equal(22, finalB);
        }

        [Fact]
        public async Task StmOptions_ReadWrite_UpdatesValue()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            var options = new StmOptions(
                MaxAttempts: 5,
                BaseDelay: TimeSpan.FromMilliseconds(5),
                MaxDelay: TimeSpan.FromMilliseconds(50),
                Strategy: BackoffType.ExponentialWithJitter,
                Mode: TransactionMode.ReadWrite);

            await STMEngine.Atomic<int>(tx =>
            {
                var v = tx.Read(x);
                tx.Write(x, v + 1);
            },
            options);

            var final = await ReadInsideTransactionAsync(x);
            Assert.Equal(1, final);
        }

        [Fact]
        public async Task StmOptions_ReadOnly_DisallowsWrite()
        {
            ResetStats();
            var x = new STMVariable<int>(10);

            var options = StmOptions.ReadOnly;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                STMEngine.Atomic<int>(tx =>
                {
                    var v = tx.Read(x);
                    tx.Write(x, v + 1); // must throw due to read-only mode in options
                },
                options));
        }

        [Fact]
        public async Task ResetStats_ClearsCounters()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            // Generate some conflicts/retries under contention
            await WithTimeout(async ct =>
            {
                var start = NewStartGate();

                var t1 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(x);
                        tx.Write(x, v + 1);
                    },
                    maxAttempts: 5,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct);

                var t2 = Task.Run(async () =>
                {
                    await start.Task;
                    await STMEngine.Atomic<int>(tx =>
                    {
                        var v = tx.Read(x);
                        tx.Write(x, v + 1);
                    },
                    maxAttempts: 5,
                    initialBackoffMilliseconds: 1,
                    backoffType: BackoffType.ExponentialWithJitter,
                    cancellationToken: ct);
                }, ct);

                start.SetResult(true);
                await Task.WhenAll(t1, t2);
            });

            // Ensure we have some values (non-deterministic but >= 0)
            var retriesBeforeReset = STMDiagnostics.GetRetryCount<int>();
            var conflictsBeforeReset = STMDiagnostics.GetConflictCount<int>();
            Assert.True(retriesBeforeReset >= 0);
            Assert.True(conflictsBeforeReset >= 0);

            // Reset and verify
            ResetStats();
            Assert.Equal(0, STMDiagnostics.GetRetryCount<int>());
            Assert.Equal(0, STMDiagnostics.GetConflictCount<int>());
        }

        [Fact]
        public void NonTransactionalWrite_DoesNotLeaveOddVersion()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            x.Write(1);

            Assert.True((x.Version & 1L) == 0, $"Version should be even after Write, but was {x.Version}.");

            var (value, version) = x.ReadWithVersion();
            Assert.Equal(1, value);
            Assert.True((version & 1L) == 0, $"Snapshot version should be even, but was {version}.");
        }

        [Fact]
        public void IncrementVersion_PreservesEvenParity_AndAdvances()
        {
            ResetStats();
            var x = new STMVariable<int>(0);

            var v0 = x.Version;
            Assert.True((v0 & 1L) == 0, $"Initial version should be even, but was {v0}.");

            x.IncrementVersion();

            var v1 = x.Version;
            Assert.True((v1 & 1L) == 0, $"Version should remain even after IncrementVersion, but was {v1}.");
            Assert.True(v1 > v0, $"Version should increase. Before={v0}, After={v1}.");
        }
    }
}