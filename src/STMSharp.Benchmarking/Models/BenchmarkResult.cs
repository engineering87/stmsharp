using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace STMSharp.Benchmarking.Models
{
    public class BenchmarkResult
    {
        public string Mode { get; set; } = "";
        public long DurationMs { get; set; }
        public double TimePerOperation { get; set; }
        public int FinalValue { get; set; }
        public long ConflictCount { get; set; }
        public long RetryCount { get; set; }
    }
}
