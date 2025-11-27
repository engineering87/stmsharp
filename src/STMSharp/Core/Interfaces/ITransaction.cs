// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Core.Interfaces
{
    /// <summary>
    /// Public transactional context used inside STMEngine.Atomic.
    /// Exposes only high-level Read/Write on STMVariable{T}.
    /// </summary>
    public interface ITransaction<T>
    {
        T Read(STMVariable<T> variable);
        void Write(STMVariable<T> variable, T value);
    }
}