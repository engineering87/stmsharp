// (c) 2024-2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core;

namespace STMSharp.Tests
{
    public class STMVariableTests
    {
        [Fact]
        public void InitialValue_IsReadCorrectly()
        {
            var x = new STMVariable<int>(42);
            Assert.Equal(42, x.Read());
        }

        [Fact]
        public void InitialVersion_IsZero()
        {
            var x = new STMVariable<int>(0);
            Assert.Equal(0, x.Version);
        }

        [Fact]
        public void Write_UpdatesValueAndAdvancesVersion()
        {
            var x = new STMVariable<int>(0);
            var v0 = x.Version;

            x.Write(10);

            Assert.Equal(10, x.Read());
            // The version is a monotonic global commit stamp; it must advance on a change.
            Assert.True(x.Version > v0);
        }

        [Fact]
        public void Write_SameValue_IsNoOp()
        {
            var x = new STMVariable<int>(5);
            var vBefore = x.Version;

            x.Write(5); // same value -> fast-path, no version change

            Assert.Equal(5, x.Read());
            Assert.Equal(vBefore, x.Version);
        }

        [Fact]
        public void ReadWithVersion_ReturnsConsistentSnapshot()
        {
            var x = new STMVariable<int>(99);

            var (value, version) = x.ReadWithVersion();

            Assert.Equal(99, value);
            Assert.Equal(x.Version, version);
        }

        [Fact]
        public void IncrementVersion_AdvancesVersion()
        {
            var x = new STMVariable<int>(0);
            var v0 = x.Version;

            x.IncrementVersion();

            Assert.True(x.Version > v0);
        }

        [Fact]
        public void ConcurrentDirectWrites_LeaveVariableReadableAndUnlocked()
        {
            var x = new STMVariable<int>(0);
            const int writers = 16;
            const int writesPerThread = 100;

            var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
            {
                for (int j = 0; j < writesPerThread; j++)
                {
                    x.Write(i * writesPerThread + j);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            // If any writer had left the variable reserved, ReadWithVersion would spin forever;
            // its completion confirms there is no stuck reservation.
            var (value, version) = x.ReadWithVersion();
            Assert.InRange(value, 0, writers * writesPerThread - 1);
            Assert.True(version > 0);
        }

        [Fact]
        public void ReferenceType_NullValue_RoundTrips()
        {
            var x = new STMVariable<string>("hello");
            x.Write(null!);

            Assert.Null(x.Read());

            var (value, _) = x.ReadWithVersion();
            Assert.Null(value);
        }

        [Fact]
        public void ReferenceType_InitialNull_IsReadCorrectly()
        {
            var x = new STMVariable<string>(null!);
            Assert.Null(x.Read());
        }
    }
}
