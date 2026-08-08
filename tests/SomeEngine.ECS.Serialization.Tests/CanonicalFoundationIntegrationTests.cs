using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using SomeEngine.ECS.Components;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public struct CanonicalFoundationPrimitives : IComponent
{
    public bool Boolean;
    public byte Byte;
    public sbyte SByte;
    public short Int16;
    public ushort UInt16;
    public int Int32;
    public uint UInt32;
    public long Int64;
    public ulong UInt64;
    public char Char;
    public float Single;
    public double Double;
}

public struct CanonicalFoundationPrimitivesCodec : ICanonicalComponentCodec<CanonicalFoundationPrimitives>
{
    public void Write(ref DataWriter writer, in CanonicalFoundationPrimitives value)
    {
        writer.WriteBoolean(value.Boolean);
        writer.WriteByte(value.Byte);
        writer.WriteSByte(value.SByte);
        writer.WriteInt16(value.Int16);
        writer.WriteUInt16(value.UInt16);
        writer.WriteInt32(value.Int32);
        writer.WriteUInt32(value.UInt32);
        writer.WriteInt64(value.Int64);
        writer.WriteUInt64(value.UInt64);
        writer.WriteChar(value.Char);
        writer.WriteSingle(value.Single);
        writer.WriteDouble(value.Double);
    }

    public void Read(ref DataReader reader, out CanonicalFoundationPrimitives value)
    {
        value = new CanonicalFoundationPrimitives
        {
            Boolean = reader.ReadBoolean(),
            Byte = reader.ReadByte(),
            SByte = reader.ReadSByte(),
            Int16 = reader.ReadInt16(),
            UInt16 = reader.ReadUInt16(),
            Int32 = reader.ReadInt32(),
            UInt32 = reader.ReadUInt32(),
            Int64 = reader.ReadInt64(),
            UInt64 = reader.ReadUInt64(),
            Char = reader.ReadChar(),
            Single = reader.ReadSingle(),
            Double = reader.ReadDouble(),
        };
    }
}

public struct CanonicalFoundationReferenceFields : IComponent
{
    public Guid Id;
    public string? Name;
}

public struct CanonicalFoundationReferenceFieldsCodec : ICanonicalComponentCodec<CanonicalFoundationReferenceFields>
{
    public void Write(ref DataWriter writer, in CanonicalFoundationReferenceFields value)
    {
        writer.WriteGuid(value.Id);
        writer.WriteString(value.Name);
    }

    public void Read(ref DataReader reader, out CanonicalFoundationReferenceFields value)
    {
        value = new CanonicalFoundationReferenceFields
        {
            Id = reader.ReadGuid(),
            Name = reader.ReadString(),
        };
    }
}

public sealed class CanonicalFoundationIntegrationTests
{
    [Fact]
    public void EcsReadBudgetHasNoBinaryDocumentLimitProjection()
    {
        Assert.Null(typeof(SerializationReadLimits).GetMethod(
            "ToBinaryReadLimits",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.Null(typeof(SerializationReadBudget).GetProperty(
            "BinaryLimits",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void FixedWidthComponentPayloadMatchesSharedCanonicalPrimitiveEncoding()
    {
        var value = new CanonicalFoundationPrimitives
        {
            Boolean = true,
            Byte = 0xAB,
            SByte = -12,
            Int16 = -12_345,
            UInt16 = 54_321,
            Int32 = -123_456_789,
            UInt32 = 3_456_789_012,
            Int64 = -1_234_567_890_123_456_789,
            UInt64 = 17_000_000_000_000_000_000,
            Char = '\u754C',
            Single = -123.25f,
            Double = Math.PI,
        };
        var registry = new SerializationRegistry()
            .Register<CanonicalFoundationPrimitives, CanonicalFoundationPrimitivesCodec>();

        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, value, registry);
        byte[] envelope = stream.ToArray();
        (_, byte[] payload) = ExtractComponentPayload(envelope);

        byte[] expected = new byte[payload.Length];
        var canonical = new BinaryDataWriter(expected);
        canonical.WriteBoolean(value.Boolean);
        canonical.WriteByte(value.Byte);
        canonical.WriteSByte(value.SByte);
        canonical.WriteInt16(value.Int16);
        canonical.WriteUInt16(value.UInt16);
        canonical.WriteInt32(value.Int32);
        canonical.WriteUInt32(value.UInt32);
        canonical.WriteInt64(value.Int64);
        canonical.WriteUInt64(value.UInt64);
        canonical.WriteChar(value.Char);
        canonical.WriteSingle(value.Single);
        canonical.WriteDouble(value.Double);

        Assert.Equal(expected.Length, canonical.WrittenCount);
        Assert.Equal(expected, payload);

        stream.Position = 0;
        CanonicalFoundationPrimitives decoded =
            WorldSerializer.ReadComponent<CanonicalFoundationPrimitives>(stream, registry);
        Assert.Equal(value.Boolean, decoded.Boolean);
        Assert.Equal(value.Byte, decoded.Byte);
        Assert.Equal(value.SByte, decoded.SByte);
        Assert.Equal(value.Int16, decoded.Int16);
        Assert.Equal(value.UInt16, decoded.UInt16);
        Assert.Equal(value.Int32, decoded.Int32);
        Assert.Equal(value.UInt32, decoded.UInt32);
        Assert.Equal(value.Int64, decoded.Int64);
        Assert.Equal(value.UInt64, decoded.UInt64);
        Assert.Equal(value.Char, decoded.Char);
        Assert.Equal(BitConverter.SingleToInt32Bits(value.Single), BitConverter.SingleToInt32Bits(decoded.Single));
        Assert.Equal(BitConverter.DoubleToInt64Bits(value.Double), BitConverter.DoubleToInt64Bits(decoded.Double));
    }

    [Fact]
    public void EcsV4GuidAndStringBytesUseCanonicalSinglePassLayout()
    {
        var value = new CanonicalFoundationReferenceFields
        {
            Id = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF"),
            Name = "current-\u4E16\u754C",
        };
        var registry = new SerializationRegistry()
            .Register<CanonicalFoundationReferenceFields, CanonicalFoundationReferenceFieldsCodec>();

        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, value, registry);
        (_, byte[] payload) = ExtractComponentPayload(stream.ToArray());

        using var expectedStream = new MemoryStream();
        using (var current = new BinaryWriter(
                   expectedStream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                   leaveOpen: true))
        {
            Span<byte> guidBytes = stackalloc byte[16];
            BinaryPrimitiveEncoding.WriteGuid(guidBytes, value.Id);
            current.Write(guidBytes);
            current.Write(true);
            current.Write(value.Name!.Length);
            current.Write(Encoding.UTF8.GetBytes(value.Name));
        }

        Assert.Equal(expectedStream.ToArray(), payload);
    }

    [Fact]
    public void SharedCanonicalBooleanValidationRejectsNonCanonicalWireValue()
    {
        var registry = new SerializationRegistry()
            .Register<CanonicalFoundationPrimitives, CanonicalFoundationPrimitivesCodec>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new CanonicalFoundationPrimitives(), registry);
        byte[] envelope = stream.ToArray();
        (int payloadOffset, _) = ExtractComponentPayload(envelope);
        envelope[payloadOffset] = 2;

        using var corrupted = new MemoryStream(envelope, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<CanonicalFoundationPrimitives>(corrupted, registry));
        Assert.Contains("Invalid Boolean value 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentStringEnvelopeUsesStreamingUtf8ReaderAndEcsDomainLimits()
    {
        var registry = new SerializationRegistry()
            .Register<CanonicalFoundationReferenceFields, CanonicalFoundationReferenceFieldsCodec>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(
            stream,
            new CanonicalFoundationReferenceFields { Name = "four" },
            registry);

        stream.Position = 0;
        var limits = SerializationReadLimits.Default with { MaxStringBytes = 3 };
        InvalidDataException limitError = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<CanonicalFoundationReferenceFields>(stream, registry, limits));
        Assert.Contains("string character", limitError.Message, StringComparison.OrdinalIgnoreCase);

        byte[] envelope = stream.ToArray();
        (int payloadOffset, byte[] payload) = ExtractComponentPayload(envelope);
        int stringOffset = payloadOffset + 16;
        Assert.Equal(1, envelope[stringOffset]);
        Assert.Equal(4, BinaryPrimitiveEncoding.ReadInt32(envelope.AsSpan(stringOffset + 1)));
        envelope[stringOffset + 5] = 0xC3;
        envelope[stringOffset + 6] = 0x28;

        using var malformed = new MemoryStream(envelope, writable: false);
        InvalidDataException utf8Error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<CanonicalFoundationReferenceFields>(malformed, registry));
        Assert.Contains("Serialized string contains malformed UTF-8", utf8Error.Message, StringComparison.Ordinal);
        Assert.Equal(25, payload.Length);
    }

    [Fact]
    public void RuntimeTypeIdentityAndFingerprintUseSharedCanonicalFoundations()
    {
        var registry = new SerializationRegistry()
            .Register<CanonicalFoundationPrimitives, CanonicalFoundationPrimitivesCodec>();
        SerializationTypeEntry entry = Assert.Single(registry.Entries.ToArray());
        SerializationTypeKey key = entry.TypeKey;
        string stableName = typeof(CanonicalFoundationPrimitives).FullName!;

        Guid canonicalId = BinaryTypeId.FromLogicalName("SomeEngine.ECS.Serialization:" + stableName);
        Assert.Equal(canonicalId, key.StableId);

        ulong fingerprint = BinaryFieldKey.FromName(stableName);
        Append(ref fingerprint, typeof(CanonicalFoundationPrimitives).Module.ModuleVersionId.ToByteArray());
        Mix(ref fingerprint, unchecked((uint)Unsafe.SizeOf<CanonicalFoundationPrimitives>()));
        Mix(ref fingerprint, (uint)entry.Storage);
        Mix(ref fingerprint, (uint)entry.Kind);
        Mix(ref fingerprint, RuntimeHelpers.IsReferenceOrContainsReferences<CanonicalFoundationPrimitives>() ? 1u : 0u);
        fingerprint = fingerprint == 0 ? 1 : fingerprint;

        Assert.Equal(fingerprint, key.SchemaFingerprint);
    }

    private static (int PayloadOffset, byte[] Payload) ExtractComponentPayload(byte[] envelope)
    {
        using var stream = new MemoryStream(envelope, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadBytes(16);
        int manifestCount = reader.ReadInt32();
        Assert.Equal(1, manifestCount);
        _ = reader.ReadBytes(16);
        int stableNameCharacters = reader.ReadInt32();
        Assert.True(stableNameCharacters > 0);
        _ = reader.ReadBytes(stableNameCharacters);
        _ = reader.ReadUInt64();
        Assert.Equal(0, reader.ReadInt32());
        int payloadOffset = checked((int)stream.Position);
        int payloadLength = checked(envelope.Length - payloadOffset - sizeof(int));
        byte[] payload = reader.ReadBytes(payloadLength);
        Assert.Equal(payloadLength, payload.Length);
        Assert.Equal(payloadLength, reader.ReadInt32());
        Assert.Equal(stream.Length, stream.Position);
        return (payloadOffset, payload);
    }

    private static void Append(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * 1099511628211UL;
    }

    private static void Mix(ref ulong hash, uint value) =>
        hash = (hash ^ value) * 1099511628211UL;
}
