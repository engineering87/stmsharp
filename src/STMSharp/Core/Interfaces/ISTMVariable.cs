// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Interface representing a transactional memory system.
    /// </summary>
    public interface ISTMVariable<T>
    {
        T Read();
        void Write(T value);
        int Version { get; }
    }
}
