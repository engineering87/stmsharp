// (c) 2024 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
using STMSharp.Core.Interfaces;

namespace STMSharp.Core
{
    /// <summary>
    /// A transactional variable that can be read and written atomically within a transaction.
    /// </summary>
    public class STMVariable<T> : ISTMVariable<T>
    {
        private T _value;
        private int _version;

        public STMVariable(T initialValue)
        {
            _value = initialValue;
            _version = 0;
        }

        /// <summary>
        /// Reads the value of the transactional variable.
        /// </summary>
        public T Read()
        {
            return _value;
        }

        /// <summary>
        /// Writes a new value to the transactional variable.
        /// </summary>
        public void Write(T value)
        {
            _value = value;
            _version++;
        }

        public int Version => _version;
    }
}
