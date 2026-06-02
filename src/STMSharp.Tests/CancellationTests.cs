// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;
using STMSharp.Enum;

namespace STMSharp.Tests
{
    [Collection("STM non-parallel")]
    public class CancellationTests
    {
        [Fact]
        public async Task Atomic_PreCancelledToken_ThrowsOperationCancelledException()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                STMEngine.Atomic<int>(
                    tx => { tx.Read(new STMVariable<int>(0)); },
                    cancellationToken: cts.Token));
        }

        [Fact]
        public async Task Atomic_CancellationDuringBackoff_ThrowsOperationCancelledException()
        {
            var shared = new STMVariable<int>(0);

            // Force a conflict so the engine enters the backoff delay
            // Use a version bump to guarantee conflict on first attempt.
            using var cts = new CancellationTokenSource();

            var task = STMEngine.Atomic<int>(async tx =>
            {
                var v = tx.Read(shared);

                // After reading, bump the version externally to force a conflict
                shared.IncrementVersion();

                tx.Write(shared, v + 1);
            },
            maxAttempts: 100,
            initialBackoffMilliseconds: 5000, // long backoff so cancellation fires during delay
            backoffType: BackoffType.Constant,
            cancellationToken: cts.Token);

            // Cancel after a short delay
            cts.CancelAfter(200);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        [Fact]
        public async Task Atomic_WithOptions_PreCancelledToken_ThrowsOperationCancelledException()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var options = new StmOptions(
                MaxAttempts: 10,
                BaseDelay: TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                STMEngine.Atomic<int>(
                    tx => { tx.Read(new STMVariable<int>(0)); },
                    options,
                    cts.Token));
        }
    }
}
