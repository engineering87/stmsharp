// (c) 2025 Francesco Del Re <francesco.delre.87@gmail.com>
// This code is licensed under MIT license (see LICENSE.txt for details)
namespace STMSharp.Enum
{
    /// <summary>
    /// Specifies the types of backoff strategies available
    /// </summary>
    public enum BackoffType
    {
        Exponential,
        ExponentialWithJitter,
        Linear,
        Constant
    }
}
