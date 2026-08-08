using System.Buffers.Binary;

namespace SomeEngine.Serialization;

/// <summary>
/// Canonical fixed-width primitive encoding shared by generated contracts and lower-level
/// serializers. Multi-byte values are little-endian; GUIDs use RFC/network byte order.
/// </summary>
public static class BinaryPrimitiveEncoding
{
    public static bool ReadBoolean(ReadOnlySpan<byte> source)
    {
        byte value = ReadByte(source);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"Invalid Boolean value {value}."),
        };
    }

    public static byte ReadByte(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(byte));
        return source[0];
    }

    public static sbyte ReadSByte(ReadOnlySpan<byte> source) => unchecked((sbyte)ReadByte(source));

    public static short ReadInt16(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(short));
        return BinaryPrimitives.ReadInt16LittleEndian(source);
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(source);
    }

    public static int ReadInt32(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(source);
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(source);
    }

    public static long ReadInt64(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(long));
        return BinaryPrimitives.ReadInt64LittleEndian(source);
    }

    public static ulong ReadUInt64(ReadOnlySpan<byte> source)
    {
        RequireSource(source, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(source);
    }

    public static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(ReadInt32(source));

    public static double ReadDouble(ReadOnlySpan<byte> source) =>
        BitConverter.Int64BitsToDouble(ReadInt64(source));

    public static char ReadChar(ReadOnlySpan<byte> source) => (char)ReadUInt16(source);

    public static Guid ReadGuid(ReadOnlySpan<byte> source)
    {
        RequireSource(source, 16);
        return new Guid(source[..16], bigEndian: true);
    }

    public static void WriteBoolean(Span<byte> destination, bool value) =>
        WriteByte(destination, value ? (byte)1 : (byte)0);

    public static void WriteByte(Span<byte> destination, byte value)
    {
        RequireDestination(destination, sizeof(byte));
        destination[0] = value;
    }

    public static void WriteSByte(Span<byte> destination, sbyte value) =>
        WriteByte(destination, unchecked((byte)value));

    public static void WriteInt16(Span<byte> destination, short value)
    {
        RequireDestination(destination, sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(destination, value);
    }

    public static void WriteUInt16(Span<byte> destination, ushort value)
    {
        RequireDestination(destination, sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
    }

    public static void WriteInt32(Span<byte> destination, int value)
    {
        RequireDestination(destination, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
    }

    public static void WriteUInt32(Span<byte> destination, uint value)
    {
        RequireDestination(destination, sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
    }

    public static void WriteInt64(Span<byte> destination, long value)
    {
        RequireDestination(destination, sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
    }

    public static void WriteUInt64(Span<byte> destination, ulong value)
    {
        RequireDestination(destination, sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
    }

    public static void WriteSingle(Span<byte> destination, float value) =>
        WriteInt32(destination, BitConverter.SingleToInt32Bits(value));

    public static void WriteDouble(Span<byte> destination, double value) =>
        WriteInt64(destination, BitConverter.DoubleToInt64Bits(value));

    public static void WriteChar(Span<byte> destination, char value) => WriteUInt16(destination, value);

    public static void WriteGuid(Span<byte> destination, Guid value)
    {
        RequireDestination(destination, 16);
        if (!value.TryWriteBytes(destination[..16], bigEndian: true, out int written) || written != 16)
            throw new InvalidOperationException("Unable to encode GUID.");
    }

    private static void RequireSource(ReadOnlySpan<byte> source, int required)
    {
        if (source.Length < required)
            throw new InvalidDataException($"Truncated primitive payload: required {required} bytes, found {source.Length}.");
    }

    private static void RequireDestination(Span<byte> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Primitive destination requires {required} bytes, but only {destination.Length} were supplied.",
                nameof(destination));
        }
    }
}
