using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;
using System.Text;

namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Reads one ECS component payload directly from its bounded stream. Fixed-width values delegate
/// to the shared <see cref="BinaryPrimitiveEncoding"/> contract; Entity/external-reference helpers,
/// nullable framing, and <see cref="SerializationReadLimits"/> accounting remain ECS-domain policy.
/// This adapter never materializes a component payload merely to use the span-backed binary-document
/// reader.
/// </summary>
public ref struct DataReader
{
    private readonly BinaryReader _reader;
    private readonly SerializationReadBudget? _budget;

    internal DataReader(BinaryReader reader, SerializationReadBudget? budget = null)
    {
        _reader = reader;
        _budget = budget;
    }

    internal BinaryReader Reader => _reader;

    internal SerializationReadBudget? Budget => _budget;

    public bool ReadBoolean()
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        ReadExactBytes(_reader, bytes, "Boolean payload");
        return BinaryPrimitiveEncoding.ReadBoolean(bytes);
    }

    public byte ReadByte()
    {
        Span<byte> bytes = stackalloc byte[sizeof(byte)];
        ReadExactBytes(_reader, bytes, "Byte payload");
        return BinaryPrimitiveEncoding.ReadByte(bytes);
    }

    public sbyte ReadSByte()
    {
        Span<byte> bytes = stackalloc byte[sizeof(sbyte)];
        ReadExactBytes(_reader, bytes, "SByte payload");
        return BinaryPrimitiveEncoding.ReadSByte(bytes);
    }

    public short ReadInt16()
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        ReadExactBytes(_reader, bytes, "Int16 payload");
        return BinaryPrimitiveEncoding.ReadInt16(bytes);
    }

    public ushort ReadUInt16()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        ReadExactBytes(_reader, bytes, "UInt16 payload");
        return BinaryPrimitiveEncoding.ReadUInt16(bytes);
    }

    public int ReadInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        ReadExactBytes(_reader, bytes, "Int32 payload");
        return BinaryPrimitiveEncoding.ReadInt32(bytes);
    }

    public uint ReadUInt32()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        ReadExactBytes(_reader, bytes, "UInt32 payload");
        return BinaryPrimitiveEncoding.ReadUInt32(bytes);
    }

    public long ReadInt64()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadExactBytes(_reader, bytes, "Int64 payload");
        return BinaryPrimitiveEncoding.ReadInt64(bytes);
    }

    public ulong ReadUInt64()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ReadExactBytes(_reader, bytes, "UInt64 payload");
        return BinaryPrimitiveEncoding.ReadUInt64(bytes);
    }

    public char ReadChar()
    {
        Span<byte> bytes = stackalloc byte[sizeof(char)];
        ReadExactBytes(_reader, bytes, "Char payload");
        return BinaryPrimitiveEncoding.ReadChar(bytes);
    }

    public float ReadSingle()
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        ReadExactBytes(_reader, bytes, "Single payload");
        return BinaryPrimitiveEncoding.ReadSingle(bytes);
    }

    public double ReadDouble()
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        ReadExactBytes(_reader, bytes, "Double payload");
        return BinaryPrimitiveEncoding.ReadDouble(bytes);
    }
    public Guid ReadGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        ReadExactBytes(_reader, bytes, "GUID payload");
        return BinaryPrimitiveEncoding.ReadGuid(bytes);
    }

    public string? ReadString()
    {
        bool hasValue = ReadBoolean();
        return hasValue
            ? SerializationBinary.ReadString(_reader, _budget, stableName: false)
            : null;
    }

    public Entity ReadEntity()
    {
        return new Entity(ReadInt32(), ReadInt32());
    }

    public ExternalReferenceKey ReadExternalReference() => new(ReadGuid());

    internal void ReadRawBytes(Span<byte> destination)
    {
        ReadExactBytes(_reader, destination, "raw byte payload");
    }

    internal int ReadBufferElementCount<T>()
        where T : struct
    {
        int count = ReadInt32();
        if (_budget is null)
        {
            if (count < 0)
                throw new InvalidDataException("Negative buffer element count.");
            return count;
        }

        return _budget.BufferElementCount<T>(count);
    }

    private static void ReadExactBytes(BinaryReader reader, Span<byte> destination, string payloadName)
    {
        int bytesRead = 0;
        while (bytesRead < destination.Length)
        {
            int read = reader.Read(destination[bytesRead..]);
            if (read == 0)
                throw new InvalidDataException($"Truncated {payloadName}.");

            bytesRead += read;
        }
    }
}

/// <summary>
/// Current ECS-v4 string framing over a stream. It shares the canonical primitive representation
/// while retaining the ECS character/byte budget and nullable marker defined by component codecs.
/// </summary>
internal static class SerializationBinary
{
    private const int Utf8BufferSize = 512;

    internal static Encoding StrictUtf8 { get; } = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string ReadString(
        BinaryReader reader,
        SerializationReadBudget? budget,
        bool stableName)
    {
        int charCount = ReadCanonicalInt32(reader, "serialized string character count");
        if (charCount < 0)
            throw new InvalidDataException("Negative serialized string character count.");
        if (budget is not null)
            budget.StringCharacterCount(charCount, stableName);

        try
        {
            return string.Create(
                charCount,
                (Reader: reader, Budget: budget, StableName: stableName),
                static (destination, state) =>
                    DecodeString(
                        state.Reader,
                        state.Budget,
                        state.StableName,
                        destination));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Serialized string contains malformed UTF-8.", exception);
        }
    }

    internal static void WriteString(BinaryWriter writer, string value)
    {
        WriteCanonicalInt32(writer, value.Length);

        Encoder encoder = StrictUtf8.GetEncoder();
        Span<byte> buffer = stackalloc byte[Utf8BufferSize];
        int characterOffset = 0;
        bool completed = false;
        while (!completed)
        {
            encoder.Convert(
                value.AsSpan(characterOffset),
                buffer,
                flush: true,
                out int charactersUsed,
                out int bytesUsed,
                out completed);
            if (charactersUsed == 0 && bytesUsed == 0 && !completed)
                throw new InvalidOperationException("UTF-8 encoder made no progress.");

            writer.Write(buffer[..bytesUsed]);
            characterOffset += charactersUsed;
        }

        if (characterOffset != value.Length)
            throw new InvalidOperationException("UTF-8 encoder did not consume its declared character count.");
    }

    private static void DecodeString(
        BinaryReader reader,
        SerializationReadBudget? budget,
        bool stableName,
        Span<char> destination)
    {
        Decoder decoder = StrictUtf8.GetDecoder();
        Span<byte> nextByte = stackalloc byte[1];
        int charactersWritten = 0;
        int bytesRead = 0;
        while (charactersWritten < destination.Length)
        {
            if (reader.Read(nextByte) == 0)
                throw new InvalidDataException("Truncated serialized string payload.");
            bytesRead = checked(bytesRead + 1);

            decoder.Convert(
                nextByte,
                destination[charactersWritten..],
                flush: false,
                out int bytesUsed,
                out int charactersUsed,
                out _);
            if (bytesUsed != 1)
            {
                throw new InvalidDataException(
                    "Serialized string character count splits a UTF-8 scalar value.");
            }
            charactersWritten = checked(charactersWritten + charactersUsed);
        }

        decoder.Convert(
            ReadOnlySpan<byte>.Empty,
            Span<char>.Empty,
            flush: true,
            out int trailingBytes,
            out int trailingCharacters,
            out bool completed);
        if (!completed || trailingBytes != 0 || trailingCharacters != 0)
        {
            throw new InvalidDataException(
                "Serialized string character count does not match its UTF-8 payload.");
        }

        budget?.StringBytesConsumed(bytesRead, stableName);
    }

    private static int ReadCanonicalInt32(BinaryReader reader, string description)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = reader.Read(bytes[offset..]);
            if (read == 0)
                throw new InvalidDataException($"Truncated {description}.");
            offset += read;
        }

        return BinaryPrimitiveEncoding.ReadInt32(bytes);
    }

    private static void WriteCanonicalInt32(BinaryWriter writer, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitiveEncoding.WriteInt32(bytes, value);
        writer.Write(bytes);
    }
}

