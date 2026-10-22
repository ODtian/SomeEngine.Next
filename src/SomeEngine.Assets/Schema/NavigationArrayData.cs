using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Assets.Schema;

internal static class NavigationArrayData
{
    internal static ReadOnlyMemory<byte> Encode<T>(ReadOnlyMemory<T> values) where T : unmanaged
    {
        var result = new byte[1][];
        JobSystem.Schedule(new EncodeArray<T>(values, result),
            [JobResourceAccess.Read(values), JobResourceAccess.Write(result)]).Complete();
        return result[0];
    }

    internal static T[] Decode<T>(ReadOnlyMemory<byte> bytes) where T : unmanaged
    {
        var result = new T[1][];
        JobSystem.Schedule(new DecodeArray<T>(bytes, result),
            [JobResourceAccess.Read(bytes), JobResourceAccess.Write(result)]).Complete();
        return result[0];
    }

    private readonly struct EncodeArray<T>(ReadOnlyMemory<T> values, byte[][] result) : IJob where T : unmanaged
    {
        public void Execute() => result[0] = MemoryMarshal.AsBytes(values.Span).ToArray();
    }

    private readonly struct DecodeArray<T>(ReadOnlyMemory<byte> bytes, T[][] result) : IJob where T : unmanaged
    {
        public void Execute() => result[0] = MemoryMarshal.Cast<byte, T>(bytes.Span).ToArray();
    }
}
