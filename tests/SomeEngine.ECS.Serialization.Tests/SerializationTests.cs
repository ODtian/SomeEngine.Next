using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;
using Xunit;
using DefaultHierarchy = SomeEngine.ECS.Hierarchy.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public struct SerPosition : SomeEngine.ECS.IComponent
{
    public float X;
    public float Y;
}

public struct SerVelocity : SomeEngine.ECS.IComponent
{
    public float X;
    public float Y;
}

public struct SerDifferentRawAbi : SomeEngine.ECS.IComponent
{
    public long Value;
    public int Revision;
}

public struct SerName : SomeEngine.ECS.IComponent
{
    public string? Value;
    public int Id;
}

public struct SerVisible : SomeEngine.ECS.IEnableableComponent
{
    public int Value;
}

public struct SerPlayerTag : SomeEngine.ECS.Components.ITag { }

public struct SerEnemyTag : SomeEngine.ECS.Components.ITag { }

public struct SerScene : SomeEngine.ECS.Components.ISharedComponent, IEquatable<SerScene>
{
    public int Value;

    public bool Equals(SerScene other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SerScene other && Equals(other);
    public override int GetHashCode() => Value;
}

[BufferCapacity(2)]
public struct SerElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct SerSparse : SomeEngine.ECS.Components.ISparseComponent
{
    public int Value;
}

public struct SerRelation : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct SerTopologySource : SomeEngine.ECS.Components.IRelationshipSource
{
    public Entity Target;
}

public struct SerTopologyTarget : SomeEngine.ECS.Components.IRelationshipTarget
{
    public int DerivedCount;
}

public struct SerExternal : SomeEngine.ECS.IComponent
{
    public ExternalReferenceKey Id;
}

[SerializableComponent("55555555-5555-5555-5555-555555555555")]
public partial struct GeneratedNestedRef : SomeEngine.ECS.IComponent
{
    public Entity Target;
}

[SerializableComponent("66666666-6666-6666-6666-666666666666")]
public partial struct GeneratedManagedRefComponent : SomeEngine.ECS.IComponent
{
    public int Value;
    public string? Name;
    public Entity Target;
    public GeneratedNestedRef Nested;
}

public enum GeneratedWideEnum : ulong
{
    Maximum = ulong.MaxValue,
}

[SerializableComponent("DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD")]
public partial struct GeneratedIntegerWidths : SomeEngine.ECS.IComponent
{
    public sbyte SByte;
    public byte Byte;
    public short Int16;
    public ushort UInt16;
    public int Int32;
    public uint UInt32;
    public long Int64;
    public ulong UInt64;
    public char Char;
    public GeneratedWideEnum Enum;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SerializableComponent("DADADADA-DADA-DADA-DADA-DADADADADADA")]
public partial struct GeneratedPackedPrimitive : SomeEngine.ECS.IComponent
{
    public int X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct SerPaddedCanonical : SomeEngine.ECS.IComponent
{
    public byte Prefix;
    public int Value;
}

public struct SerPaddedCanonicalCodec : ICanonicalComponentCodec<SerPaddedCanonical>
{
    public void Write(ref DataWriter writer, in SerPaddedCanonical value)
    {
        writer.WriteByte(value.Prefix);
        writer.WriteInt32(value.Value);
    }

    public void Read(ref DataReader reader, out SerPaddedCanonical value)
    {
        value = new SerPaddedCanonical
        {
            Prefix = reader.ReadByte(),
            Value = reader.ReadInt32(),
        };
    }
}

public struct SerNameCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerName>
{
    private static int _writeCount;

    public static int WriteCount => Volatile.Read(ref _writeCount);

    public static void ResetWriteCount() => Volatile.Write(ref _writeCount, 0);

    public void Write(ref DataWriter writer, in SerName value)
    {
        Interlocked.Increment(ref _writeCount);
        writer.WriteString(value.Value);
        writer.WriteInt32(value.Id);
    }

    public void Read(ref DataReader reader, out SerName value)
    {
        value = new SerName
        {
            Value = reader.ReadString(),
            Id = reader.ReadInt32(),
        };
    }
}

public struct SerPositionFullCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerPosition>
{
    private static int _readCount;

    public static int ReadCount => Volatile.Read(ref _readCount);

    public static void ResetReadCount() => Volatile.Write(ref _readCount, 0);

    public void Write(ref DataWriter writer, in SerPosition value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    public void Read(ref DataReader reader, out SerPosition value)
    {
        Interlocked.Increment(ref _readCount);
        value = new SerPosition
        {
            X = reader.ReadSingle(),
            Y = reader.ReadSingle(),
        };
    }
}

public struct SerPositionXOnlyCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerPosition>
{
    public void Write(ref DataWriter writer, in SerPosition value)
    {
        writer.WriteSingle(value.X);
    }

    public void Read(ref DataReader reader, out SerPosition value)
    {
        value = new SerPosition
        {
            X = reader.ReadSingle(),
        };
    }
}

public struct SerExternalCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerExternal>
{
    public void Write(ref DataWriter writer, in SerExternal value)
    {
        writer.WriteExternalReference(value.Id);
    }

    public void Read(ref DataReader reader, out SerExternal value)
    {
        value = new SerExternal { Id = reader.ReadExternalReference() };
    }
}

public class SerializationTests
{
    [Fact]
    public void SerializationRestore_RejectsExistingWorldBeforeResettingItsBacking()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new SerPosition { X = 17, Y = 29 });

        Assert.Throws<InvalidOperationException>(() => world.BeginSerializationStore(slotCount: 0));

        Assert.True(world.IsAlive(entity));
        Assert.True(world.Has<SerPosition>(entity));
        Assert.Equal(17, world.Read<SerPosition>(entity).X);
        Assert.Equal(29, world.Read<SerPosition>(entity).Y);
    }

    [Fact]
    public void Component_UnmanagedValue_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1.5f, Y = 2.5f }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerPosition>(stream, registry);

        Assert.Equal(1.5f, value.X);
        Assert.Equal(2.5f, value.Y);
    }

    [Fact]
    public void RawCheckpoint_ExplicitLogicalKeyStillBindsActualComponentAbi()
    {
        var logicalKey = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-00000000000A"),
            "Explicit.Raw.Abi",
            0x1020304050607080ul);
        var writeRegistry = new SerializationRegistry().Register<SerPosition>(logicalKey);
        var sameAbiRegistry = new SerializationRegistry().Register<SerPosition>(logicalKey);
        var differentAbiRegistry = new SerializationRegistry().Register<SerDifferentRawAbi>(logicalKey);
        using var stream = new MemoryStream();

        WorldSerializer.WriteCheckpointComponent(
            stream,
            new SerPosition { X = 4.5f, Y = 6.5f },
            writeRegistry);
        stream.Position = 0;
        SerPosition value = WorldSerializer.ReadCheckpointComponent<SerPosition>(stream, sameAbiRegistry);
        Assert.Equal(4.5f, value.X);
        Assert.Equal(6.5f, value.Y);

        stream.Position = 0;
        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadCheckpointComponent<SerDifferentRawAbi>(stream, differentAbiRegistry));
        Assert.Contains("Schema mismatch", error.Message, StringComparison.OrdinalIgnoreCase);

        SerializationTypeEntry bound = Assert.Single(writeRegistry.Entries.ToArray());
        Assert.NotEqual(logicalKey.SchemaFingerprint, bound.TypeKey.SchemaFingerprint);
        Assert.Equal(ComponentCodecKind.Raw, bound.CodecKind);
    }

    [Fact]
    public void DurableComponent_RejectsInvalidUtf16InsteadOfReplacingIt()
    {
        SerNameCodec.ResetWriteCount();
        var key = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-00000000000B"),
            "Strict.Utf8.Component",
            0x1122334455667788ul);
        var registry = new SerializationRegistry().Register<SerName, SerNameCodec>(key);
        using var stream = new MemoryStream();

        Assert.Throws<EncoderFallbackException>(() =>
            WorldSerializer.WriteDurableComponent(
                stream,
                new SerName { Value = "\uD800", Id = 7 },
                registry));
        Assert.Equal(1, SerNameCodec.WriteCount);
    }

    [Fact]
    public void DurableWorldRejectsUnsafeReferenceCaptureAndTopologyRejectsInvalidUtf16()
    {
        var valueKey = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-00000000000C"),
            "Strict.Utf8.World",
            0x2233445566778899ul);
        var valueRegistry = new SerializationRegistry().Register<SerName, SerNameCodec>(valueKey);
        var valueWorld = new World();
        _ = valueWorld.CreateEntity(new SerName { Value = "\uD800", Id = 8 });
        using var valueStream = new MemoryStream();
        var captureError = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteDurableWorld(valueStream, valueWorld, valueRegistry));
        Assert.Contains("deep snapshot-clone contract", captureError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, valueStream.Length);

        var topologyKey = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-00000000000D"),
            "Strict.Utf8.Topology.\uD800",
            0x33445566778899AAul);
        var topologyRegistry = new SerializationRegistry()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>(topologyKey);
        using var topologyStream = new MemoryStream();
        Assert.Throws<EncoderFallbackException>(() =>
            WorldSerializer.WriteDurableWorld(topologyStream, new World(), topologyRegistry));
    }

    [Fact]
    public void DurableSave_RejectsImplicitRawCodecBeforeWritingPayload()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();

        var error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteDurableComponent(
                stream,
                new SerPosition { X = 1, Y = 2 },
                registry));

        Assert.Contains("implicit raw codec", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void DurableSave_ManualRawCanonicalSizeWithoutProof_UsesFiveByteCanonicalCodec()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000010"),
            "SerPaddedCanonical.v1",
            0x6162636465666768ul);
        int nativeSize = Unsafe.SizeOf<SerPaddedCanonical>();
        Assert.True(nativeSize > 5);

        var registry = new SerializationRegistry()
            .RegisterCanonical<SerPaddedCanonical, SerPaddedCanonicalCodec>(
                key,
                nativeSize);
        var spoofedProofRegistry = new SerializationRegistry()
            .RegisterCanonical<SerPaddedCanonical, SerPaddedCanonicalCodec>(
                key,
                nativeSize,
                rawCanonicalLayoutFingerprint: 1);
        Assert.Equal(
            ComponentCodecKind.Canonical,
            Assert.Single(registry.Entries.ToArray()).CodecKind);
        Assert.Equal(
            ComponentCodecKind.Canonical,
            Assert.Single(spoofedProofRegistry.Entries.ToArray()).CodecKind);

        var source = new SerPaddedCanonical { Prefix = 0xA5, Value = 0x12345678 };
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in source, registry);

        stream.Position = 0;
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            Assert.Equal(0x53434553u, reader.ReadUInt32());
            Assert.Equal(4, reader.ReadUInt16());
            Assert.Equal((byte)SerializationContract.DurableSave, reader.ReadByte());
            _ = reader.ReadByte();
            Assert.Equal(16, reader.ReadBytes(16).Length);
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(16, reader.ReadBytes(16).Length);
            Assert.Equal(
                key.StableName,
                SerializationBinary.ReadString(reader, budget: null, stableName: true));
            Assert.Equal(key.SchemaFingerprint, reader.ReadUInt64());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal(source.Prefix, reader.ReadByte());
            Assert.Equal(source.Value, reader.ReadInt32());
            Assert.Equal(5, reader.ReadInt32());
            Assert.Equal(stream.Length, stream.Position);
        }

        stream.Position = 0;
        SerPaddedCanonical loaded =
            WorldSerializer.ReadDurableComponent<SerPaddedCanonical>(stream, registry);
        Assert.Equal(source.Prefix, loaded.Prefix);
        Assert.Equal(source.Value, loaded.Value);
    }

    [Fact]
    public void DurableSave_SourceGeneratedPackedProof_UsesVerifiedRawCanonicalRoundTrip()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        Guid stableId = Guid.Parse("DADADADA-DADA-DADA-DADA-DADADADADADA");
        SerializationTypeEntry entry = Assert.Single(
            registry.Entries.ToArray(),
            candidate => candidate.TypeKey.StableId == stableId);
        Assert.Equal(
            BitConverter.IsLittleEndian
                ? ComponentCodecKind.RawCanonical
                : ComponentCodecKind.Canonical,
            entry.CodecKind);

        var source = new GeneratedPackedPrimitive { X = int.MinValue, Y = 123.5f };
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in source, registry);
        stream.Position = 0;

        GeneratedPackedPrimitive loaded =
            WorldSerializer.ReadDurableComponent<GeneratedPackedPrimitive>(stream, registry);
        Assert.Equal(source.X, loaded.X);
        Assert.Equal(source.Y, loaded.Y);
    }

    [Fact]
    public void Registration_RejectsCustomCodecWithoutExplicit64BitSchemaFingerprint()
    {
        var incompleteKey = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000001"),
            "SerPosition.IncompleteCustom",
            0);

        var error = Assert.Throws<ArgumentException>(() =>
            new SerializationRegistry()
                .Register<SerPosition, SerPositionFullCodec>(incompleteKey));

        Assert.Contains("non-zero 64-bit schema fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawCheckpointRegistration_RejectsMissing64BitSchemaFingerprint()
    {
        var incompleteKey = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000002"),
            "SerPosition.IncompleteCheckpoint",
            0);

        var error = Assert.Throws<ArgumentException>(() =>
            new SerializationRegistry()
                .Register<SerPosition, SerPositionFullCodec>(incompleteKey));
        Assert.Contains("non-zero 64-bit schema fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurableSave_RejectsAutomaticCustomCodecBuildDerivedSchema()
    {
        var registry = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>();
        using var stream = new MemoryStream();

        var error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteDurableComponent(
                stream,
                new SerPosition { X = 3, Y = 4 },
                registry));

        Assert.Contains("build-derived runtime schema identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void DurableSave_ExplicitStableCustomSchema_RoundTripsCurrentSchemaFastPath()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000007"),
            "SerPosition.StableCustom",
            0xBF906D8E541A237Cul);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        using var stream = new MemoryStream();

        WorldSerializer.WriteDurableComponent(
            stream,
            new SerPosition { X = 3, Y = 4 },
            registry);

        stream.Position = 0;
        SerPosition value = WorldSerializer.ReadDurableComponent<SerPosition>(stream, registry);
        Assert.Equal(3, value.X);
        Assert.Equal(4, value.Y);
    }

    [Fact]
    public void RawCheckpoint_RejectsBuildOrAbiIdentityMismatch()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, registry);

        byte[] bytes = stream.ToArray();
        bytes[8] ^= 0x5A; // The v4 checkpoint identity begins after magic/version/contract/kind.
        using var corrupted = new MemoryStream(bytes, writable: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(corrupted, registry));
        Assert.Contains("build/ABI identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonSeekableDurableEntityAndWorld_RoundTrip()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000004"),
            "SerPosition.NonSeekable",
            0x8C6D3A5B21E7F049ul);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        var source = new World();
        Entity sourceEntity = source.CreateEntity(new SerPosition { X = 9, Y = 10 });

        using var worldBytes = new MemoryStream();
        WorldSerializer.WriteDurableWorld(worldBytes, source, registry);
        using var worldInput = new NonSeekableReadStream(worldBytes.ToArray());
        World loaded = WorldSerializer.ReadDurableWorld(worldInput, registry);
        Assert.True(loaded.IsAlive(sourceEntity));
        Assert.Equal(9, loaded.Read<SerPosition>(sourceEntity).X);
        Assert.Equal(10, loaded.Read<SerPosition>(sourceEntity).Y);
    }

    [Fact]
    public void NonSeekableDurableWorld_RejectsTrailingItemPayload()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("A1000000-0000-0000-0000-000000000005"),
            "SerPosition.Trailing",
            0x9D7E4B6C32F8015Aul);
        var writerRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        var underReadingRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionXOnlyCodec>(key);
        var source = new World();
        _ = source.CreateEntity(new SerPosition { X = 11, Y = 12 });
        using var bytes = new MemoryStream();
        WorldSerializer.WriteDurableWorld(bytes, source, writerRegistry);
        using var input = new NonSeekableReadStream(bytes.ToArray());

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadDurableWorld(input, underReadingRegistry));
        Assert.Contains("footer does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurableReader_RejectsRawCheckpointEvenWhenBuildIdentityMatches()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, registry);
        stream.Position = 0;

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(
                stream,
                registry,
                new SerializationReadOptions(RequiredContract: SerializationContract.DurableSave)));
        Assert.Contains("requires DurableSave", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsHugeManifestCountBeforeAllocation()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, registry);

        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, sizeof(int)), int.MaxValue);
        using var corrupted = new MemoryStream(bytes, writable: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(corrupted, registry));
        Assert.Contains("manifest", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsMalformedUtf8InStableTypeName()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, registry);

        byte[] bytes = stream.ToArray();
        byte[] name = Encoding.UTF8.GetBytes(typeof(SerPosition).FullName!);
        int offset = bytes.AsSpan().IndexOf(name);
        Assert.True(offset >= 0);
        bytes[offset] = 0xC3;
        bytes[offset + 1] = 0x28;
        using var corrupted = new MemoryStream(bytes, writable: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(corrupted, registry));
        Assert.Contains("malformed UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_EnforcesCumulativeStringByteBudgetBeforeNextStringAllocation()
    {
        var registry = new SerializationRegistry().Register<SerName, SerNameCodec>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(
            stream,
            new SerName { Value = "payload", Id = 1 },
            registry);
        stream.Position = 0;

        int stableNameBytes = Encoding.UTF8.GetByteCount(typeof(SerName).FullName!);
        var limits = SerializationReadLimits.Default with
        {
            MaxTotalStringBytes = stableNameBytes,
        };
        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerName>(stream, registry, limits));
        Assert.Contains("total string byte", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWorld_EnforcesTotalAllocationBudgetBeforeEntitySlotArray()
    {
        var source = new World();
        _ = source.CreateEntity();
        _ = source.CreateEntity();
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, new SerializationRegistry());
        stream.Position = 0;

        var limits = SerializationReadLimits.Default with
        {
            // Empty manifest array estimate consumes 24 bytes; the next slot-array reservation
            // must fail before allocating the two-slot array.
            MaxTotalAllocationBytes = 24,
        };
        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(
                stream,
                new SerializationRegistry(),
                new WorldLoadOptions(ReadLimits: limits)));
        Assert.Contains("entity slot array", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allocation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsInvalidPayloadFooter()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, registry);

        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(int)), int.MaxValue);
        using var corrupted = new MemoryStream(bytes, writable: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(corrupted, registry));
        Assert.Contains("footer does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsWireTypeKeyWithout64BitFingerprint()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var current = new MemoryStream();
        WorldSerializer.WriteComponent(current, new SerPosition { X = 1, Y = 2 }, registry);
        byte[] bytes = current.ToArray();
        (int fingerprintOffset, _) = FindComponentEnvelopeOffsets(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(fingerprintOffset), 0);

        using var rejected = new MemoryStream(bytes, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(rejected, registry));
        Assert.Contains("64-bit schema fingerprint", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_RejectsLegacyLengthPrefixItemFrame()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var current = new MemoryStream();
        WorldSerializer.WriteComponent(current, new SerPosition { X = 1, Y = 2 }, registry);
        byte[] bytes = current.ToArray();
        (_, int payloadOffset) = FindComponentEnvelopeOffsets(bytes);
        int footerOffset = bytes.Length - sizeof(int);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(footerOffset));
        Assert.Equal(footerOffset - payloadOffset, payloadLength);

        var legacyPrefix = new byte[bytes.Length];
        bytes.AsSpan(0, payloadOffset).CopyTo(legacyPrefix);
        bytes.AsSpan(footerOffset, sizeof(int)).CopyTo(legacyPrefix.AsSpan(payloadOffset));
        bytes.AsSpan(payloadOffset, payloadLength)
            .CopyTo(legacyPrefix.AsSpan(payloadOffset + sizeof(int)));

        using var rejected = new MemoryStream(legacyPrefix, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(rejected, registry));
        Assert.Contains("footer does not match", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWorld_EnforcesCentralBufferElementBudget()
    {
        var registry = new SerializationRegistry().RegisterBuffer<SerElement>();
        var source = new World();
        Entity entity = source.CreateEntity();
        source.AddBuffer<SerElement>(entity);
        WriteBufferValues(
            source,
            entity,
            new SerElement { Value = 1 },
            new SerElement { Value = 2 });
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, registry);
        stream.Position = 0;

        var limits = SerializationReadLimits.Default with
        {
            MaxBufferElementsPerBuffer = 1,
        };
        var error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(
                stream,
                registry,
                new WorldLoadOptions(ReadLimits: limits)));
        Assert.Contains("buffer element", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Component_ManagedValue_RequiresCodec()
    {
        var registry = new SerializationRegistry().Register<SerName>();
        using var stream = new MemoryStream();

        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteComponent(stream, new SerName { Value = "alpha", Id = 7 }, registry));
    }

    [Fact]
    public void Component_ManagedValue_WithCodec_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerName, SerNameCodec>();
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerName { Value = "beta", Id = 8 }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerName>(stream, registry);

        Assert.Equal("beta", value.Value);
        Assert.Equal(8, value.Id);
    }

    [Fact]
    public void WorldSnapshot_PreservesIdentityAndLogicalStorage()
    {
        var registry = FullRegistry();
        var world = new World();
        var live = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var dead = world.CreateEntity();
        var target = world.CreateEntity(new SerPosition { X = 9, Y = 10 });
        world.DestroyEntity(dead);

        world.Add(live, new SerVisible { Value = 5 });
        world.Disable<SerVisible>(live);
        world.AddTag<SerPlayerTag>(live);
        world.AddShared(live, new SerScene { Value = 11 });
        world.AddBuffer<SerElement>(live);
        WriteBufferValues(
            world,
            live,
            new SerElement { Value = 4 },
            new SerElement { Value = 5 },
            new SerElement { Value = 6 });
        world.AddSparse(live, new SerSparse { Value = 12 });
        world.Add(live, new SerRelation { Value = 13 });

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        WorldSerializer.WriteWorld(first, world, registry);
        WorldSerializer.WriteWorld(second, world, registry);

        Assert.Equal(first.ToArray(), second.ToArray());

        first.Position = 0;
        var loaded = WorldSerializer.ReadWorld(first, registry);

        Assert.True(loaded.IsAlive(live));
        Assert.True(loaded.IsAlive(target));
        Assert.False(loaded.IsAlive(dead));
        Assert.Equal(1, loaded.Read<SerPosition>(live).X);
        Assert.False(loaded.IsEnabled<SerVisible>(live));
        Assert.True(loaded.Has<SerPlayerTag>(live));
        Assert.Equal(11, loaded.GetShared<SerScene>(live).Value);
        Assert.Equal([4, 5, 6], ReadBufferValues(loaded, live).Select(x => x.Value));
        Assert.Equal(12, loaded.ReadSparse<SerSparse>(live).Value);
        Assert.Equal(13, loaded.Read<SerRelation>(live).Value);

        var reused = loaded.CreateEntity();
        Assert.Equal(dead.Index, reused.Index);
        Assert.NotEqual(dead, reused);
    }

    [Fact]
    public void Read_FailsForCorruptHeader()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Throws<EndOfStreamException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, registry));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_RejectsEveryPreV4FormatVersion(int legacyVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x53434553u);
            writer.Write(checked((ushort)legacyVersion));
            writer.Write((byte)0);
            writer.Write((byte)SnapshotPayloadKind.World);
            writer.Write(0);
        }

        stream.Position = 0;
        var error = Assert.Throws<InvalidDataException>(
            () => WorldSerializer.ReadWorld(stream, new SerializationRegistry()));
        Assert.Contains($"format version {legacyVersion}", error.Message);
    }

    [Fact]
    public void Read_FailsForTruncatedRawPayload()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var valid = new MemoryStream();
        WorldSerializer.WriteComponent(valid, new SerPosition { X = 1, Y = 2 }, registry);

        var bytes = valid.ToArray();
        using var truncated = new MemoryStream(bytes.AsSpan(0, bytes.Length - 4).ToArray());

        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(truncated, registry));
    }

    [Fact]
    public void Read_FailsForSchemaMismatch()
    {
        var stableId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(
                new SerializationTypeKey(stableId, "SerPosition", 1));
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(
                new SerializationTypeKey(stableId, "SerPosition", 2));
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, writeRegistry);

        SerPositionFullCodec.ResetReadCount();
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, readRegistry));
        Assert.Equal(0, SerPositionFullCodec.ReadCount);
    }

    [Fact]
    public void ReadWorld_RejectsUnknownComponentManifest()
    {
        var writeRegistry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, writeRegistry);

        stream.Position = 0;
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(stream, new SerializationRegistry()));
        Assert.Contains("Unknown serialized type", error.Message);
    }

    [Fact]
    public void Read_RejectsLegacyGuidEncodingBeforeCodec()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("12345678-9ABC-4DEF-8123-456789ABCDEF"),
            "legacy-guid",
            0x1020304050607080ul);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        byte[] envelope = CreateManualV4ComponentEnvelope(key, legacyGuidEncoding: true);

        SerPositionFullCodec.ResetReadCount();
        using var stream = new MemoryStream(envelope, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, registry));
        Assert.Contains("Unknown serialized type", error.Message);
        Assert.Equal(0, SerPositionFullCodec.ReadCount);
    }

    [Fact]
    public void Read_RejectsLegacyPrimitiveStringEncodingBeforeCodec()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("6F3A6C85-7B1F-4A93-8C2A-E9DA4E6782C1"),
            "xy",
            0x2131415161718191ul);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        byte[] envelope = CreateManualV4ComponentEnvelope(key, legacyStringEncoding: true);

        SerPositionFullCodec.ResetReadCount();
        using var stream = new MemoryStream(envelope, writable: false);
        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, registry));
        Assert.Equal(0, SerPositionFullCodec.ReadCount);
    }

    [Fact]
    public void Read_RejectsLegacy32BitSchemaFieldBeforeCodec()
    {
        var key = new SerializationTypeKey(
            Guid.Parse("8A1B37AE-F0F0-43B7-965B-72D46C28B248"),
            "legacy-schema32",
            0x31415161718191A1ul);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        byte[] envelope = CreateManualV4ComponentEnvelope(key, legacySchemaHash: 0x89ABCDEFu);

        SerPositionFullCodec.ResetReadCount();
        using var stream = new MemoryStream(envelope, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, registry));
        Assert.Contains("Schema mismatch", error.Message);
        Assert.Equal(0, SerPositionFullCodec.ReadCount);
    }

    [Fact]
    public void ReadWorld_FailsForInconsistentSlotPayloadIdentity()
    {
        InvalidDataException deadSlot = AssertInvalidWorldPayload(writer =>
        {
            writer.Write(2);
            writer.Write(1);
            writer.Write(0);
            writer.Write(true);
            writer.Write(2);
            writer.Write(0);
            writer.Write(false);

            writer.Write(1);
            writer.Write(2);
            writer.Write(0);
            writer.Write(0);
        });
        Assert.Contains("no matching live slot", deadSlot.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException generationMismatch = AssertInvalidWorldPayload(writer =>
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(true);

            writer.Write(1);
            writer.Write(1);
            writer.Write(0);
            writer.Write(0);
        });
        Assert.Contains("no matching live slot", generationMismatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadWorld_RemapMode_ImportsIntoFreshIdentities()
    {
        var registry = FullRegistry();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, world, registry);

        stream.Position = 0;
        var loaded = WorldSerializer.ReadWorld(
            stream,
            registry,
            new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap));

        Assert.Equal(1, loaded.EntityCount);
    }

    [Fact]
    public void GeneratedCodec_ManagedNestedAndEntityReferences_RoundTrip()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        using var stream = new MemoryStream();
        var idWorld = new World();
        var target = idWorld.CreateEntity();

        WorldSerializer.WriteComponent(
            stream,
            new GeneratedManagedRefComponent
            {
                Value = 12,
                Name = "generated",
                Target = target,
                Nested = new GeneratedNestedRef { Target = target },
            },
            registry,
            new SerializeOptions(Contract: SerializationContract.DurableSave));

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<GeneratedManagedRefComponent>(stream, registry);

        Assert.Equal(12, value.Value);
        Assert.Equal("generated", value.Name);
        Assert.Equal(target, value.Target);
        Assert.Equal(target, value.Nested.Target);
    }

    [Fact]
    public void GeneratedCanonicalCodec_AllFixedWidthIntegersAndUnsignedEnum_RoundTrip()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        var source = new GeneratedIntegerWidths
        {
            SByte = sbyte.MinValue,
            Byte = byte.MaxValue,
            Int16 = short.MinValue,
            UInt16 = ushort.MaxValue,
            Int32 = int.MinValue,
            UInt32 = uint.MaxValue,
            Int64 = long.MinValue,
            UInt64 = ulong.MaxValue,
            Char = '\uFFFF',
            Enum = GeneratedWideEnum.Maximum,
        };
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in source, registry);
        stream.Position = 0;

        GeneratedIntegerWidths loaded =
            WorldSerializer.ReadDurableComponent<GeneratedIntegerWidths>(stream, registry);
        Assert.Equal(source.SByte, loaded.SByte);
        Assert.Equal(source.Byte, loaded.Byte);
        Assert.Equal(source.Int16, loaded.Int16);
        Assert.Equal(source.UInt16, loaded.UInt16);
        Assert.Equal(source.Int32, loaded.Int32);
        Assert.Equal(source.UInt32, loaded.UInt32);
        Assert.Equal(source.Int64, loaded.Int64);
        Assert.Equal(source.UInt64, loaded.UInt64);
        Assert.Equal(source.Char, loaded.Char);
        Assert.Equal(source.Enum, loaded.Enum);
    }

    [Fact]
    public void ExternalReferenceKey_CodecHelper_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerExternal, SerExternalCodec>();
        var id = new ExternalReferenceKey(Guid.Parse("88888888-8888-8888-8888-888888888888"));
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerExternal { Id = id }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerExternal>(stream, registry);

        Assert.Equal(id, value.Id);
    }

    [Fact]
    public void RelationPayload_UsesOrdinaryComponentRegistrationAndRoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerRelation>();
        var source = new World();
        var payloadEntity = source.CreateEntity(new SerRelation { Value = 9 });

        string obsoleteRegistrationName = string.Concat("Register", "Relation");
        Assert.DoesNotContain(
            typeof(SerializationRegistry).GetMethods(),
            method => string.Equals(method.Name, obsoleteRegistrationName, StringComparison.Ordinal));
        var entry = Assert.Single(registry.Entries.ToArray());
        Assert.Equal(SerializationValueKind.Component, entry.Kind);
        Assert.Equal(StoragePath.Table, entry.Storage);

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, source, registry);

        snapshot.Position = 0;
        var loaded = WorldSerializer.ReadWorld(snapshot, registry);

        Assert.Equal(9, loaded.Read<SerRelation>(payloadEntity).Value);
    }

    [Fact]
    public void RelationshipTopologyComponents_CannotUseOrdinaryEntityRowRegistration()
    {
        var sourceError = Assert.Throws<InvalidOperationException>(
            () => new SerializationRegistry().Register<SerTopologySource>());
        var targetError = Assert.Throws<InvalidOperationException>(
            () => new SerializationRegistry().Register<SerTopologyTarget>());

        Assert.Contains("canonical relationship/hierarchy serialization section", sourceError.Message);
        Assert.Contains("canonical relationship/hierarchy serialization section", targetError.Message);
    }

    private static SerializationRegistry FullRegistry()
    {
        return new SerializationRegistry()
            .Register<SerPosition>()
            .Register<SerVisible>()
            .RegisterTag<SerPlayerTag>()
            .RegisterShared<SerScene>()
            .RegisterBuffer<SerElement>()
            .RegisterSparse<SerSparse>()
            .Register<SerRelation>();
    }

    private static void WriteBufferValues(
        World world,
        SomeEngine.ECS.Entities.Entity entity,
        params SerElement[] values)
    {
        world.ExecuteBufferWrite<SerElement, SerElement[]>(
            entity,
            ref values,
            static (DynamicBuffer<SerElement> buffer, ref SerElement[] source) =>
            {
                for (int i = 0; i < source.Length; i++)
                    buffer.Add(source[i]);
            });
    }

    private static SerElement[] ReadBufferValues(
        World world,
        SomeEngine.ECS.Entities.Entity entity)
    {
        SerElement[] values = null!;
        world.ExecuteBufferRead<SerElement, SerElement[]>(
            entity,
            ref values,
            static (BufferView<SerElement> buffer, ref SerElement[] destination) =>
                destination = buffer.AsSpan().ToArray());
        return values;
    }

    private static object PublishedStructureRoot(World world)
    {
        var property = typeof(World).GetProperty(
            "PublishedStructureRoot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<object>(property?.GetValue(world));
    }

    private static long PublishedStructureEpoch(World world)
    {
        var property = typeof(World).GetProperty(
            "PublishedStructureEpoch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<long>(property?.GetValue(world));
    }

    private static (int FingerprintOffset, int PayloadOffset) FindComponentEnvelopeOffsets(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(0x53434553u, reader.ReadUInt32());
        Assert.Equal((ushort)4, reader.ReadUInt16());
        _ = reader.ReadByte();
        Assert.Equal((byte)SnapshotPayloadKind.Component, reader.ReadByte());
        Assert.Equal(16, reader.ReadBytes(16).Length);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(16, reader.ReadBytes(16).Length);
        _ = SerializationBinary.ReadString(reader, budget: null, stableName: true);
        int fingerprintOffset = checked((int)stream.Position);
        _ = reader.ReadUInt64();
        Assert.Equal(0, reader.ReadInt32());
        return (fingerprintOffset, checked((int)stream.Position));
    }

    private static byte[] CreateManualV4ComponentEnvelope(
        SerializationTypeKey key,
        bool legacyGuidEncoding = false,
        bool legacyStringEncoding = false,
        uint? legacySchemaHash = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        writer.Write(0x53434553u);
        writer.Write((ushort)4);
        writer.Write((byte)SerializationContract.DurableSave);
        writer.Write((byte)SnapshotPayloadKind.Component);
        writer.Write(stackalloc byte[16]);
        writer.Write(1);

        if (legacyGuidEncoding)
        {
            writer.Write(key.StableId.ToByteArray());
        }
        else
        {
            Span<byte> stableId = stackalloc byte[16];
            SomeEngine.Serialization.BinaryPrimitiveEncoding.WriteGuid(stableId, key.StableId);
            writer.Write(stableId);
        }

        if (legacyStringEncoding)
            writer.Write(key.StableName);
        else
            SerializationBinary.WriteString(writer, key.StableName);
        if (legacySchemaHash is not null)
            writer.Write(legacySchemaHash.Value);
        writer.Write(key.SchemaFingerprint);
        writer.Write(0);
        writer.Write(1f);
        writer.Write(2f);
        writer.Write(sizeof(float) * 2);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableReadStream(byte[] bytes) =>
            _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class GateReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private int _blocked;

        internal GateReadStream(byte[] bytes) =>
            _inner = new MemoryStream(bytes, writable: false);

        internal bool WaitUntilRead(TimeSpan timeout) => _entered.Wait(timeout);
        internal void Release() => _release.Set();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BlockFirstRead();
            return _inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            BlockFirstRead();
            return _inner.Read(buffer);
        }

        public override int ReadByte()
        {
            BlockFirstRead();
            return _inner.ReadByte();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _inner.Dispose();
                _entered.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BlockFirstRead()
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0)
                return;
            _entered.Set();
            _release.Wait();
        }
    }

    private static MemoryStream CreateWorldPayload(Action<BinaryWriter> writeBody)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x53434553u);
            writer.Write((ushort)4);
            writer.Write((byte)SerializationContract.DurableSave);
            writer.Write((byte)SnapshotPayloadKind.World);
            Span<byte> checkpointIdentity = stackalloc byte[16];
            SomeEngine.Serialization.BinaryPrimitiveEncoding.WriteGuid(checkpointIdentity, Guid.Empty);
            writer.Write(checkpointIdentity);
            writer.Write(0);
            writer.Write(0u);
            writeBody(writer);
        }

        stream.Position = 0;
        return stream;
    }

    private static InvalidDataException AssertInvalidWorldPayload(Action<BinaryWriter> writeBody)
    {
        using var stream = CreateWorldPayload(writeBody);
        return Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(stream, new SerializationRegistry()));
    }
}
