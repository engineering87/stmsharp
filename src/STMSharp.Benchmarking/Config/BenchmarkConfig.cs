// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Benchmarking.Config
{
    /// <summary>
    /// Configuration class for benchmark parameters.
    /// </summary>
    public class BenchmarkConfig
    {
        public int NumberOfThreads { get; set; }
        public int NumberOfOperations { get; set; }
        public int BackoffTime { get; set; }
        public int ProcessingTime { get; set; }
    }
}
