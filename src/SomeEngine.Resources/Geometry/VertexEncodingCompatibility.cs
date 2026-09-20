using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Resources.Geometry;

public static class VertexEncodingCompatibility
{
    public static ulong Hash(string compatibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compatibility);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(compatibility), digest);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(digest);
        return value != 0 ? value : throw new InvalidOperationException(
            "A vertex encoding compatibility hash cannot use the reserved zero value.");
    }
}
