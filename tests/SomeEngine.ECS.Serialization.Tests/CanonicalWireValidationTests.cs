using System.Buffers.Binary;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class CanonicalWireValidationTests
{
    [Fact]
    public void ComponentRead_RejectsStableNameMismatchBeforeCodec()
    {
        Guid stableId = Guid.Parse("7C6BC350-39C9-4854-8676-3AB7FEF0B1E1");
        const ulong fingerprint = 0x1122334455667788;
        var registeredKey = new SerializationTypeKey(stableId, "canonical-position", fingerprint);
        var wireKey = new SerializationTypeKey(stableId, "altered-position", fingerprint);
        var registry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(registeredKey);
        byte[] envelope = CreateComponentEnvelope(wireKey);

        SerPositionFullCodec.ResetReadCount();
        using var input = new MemoryStream(envelope, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(input, registry));

        Assert.Contains("does not exactly match", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, SerPositionFullCodec.ReadCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ManifestReaders_RejectDuplicateOrNonCanonicalKeys(
        bool manifestCountOnly,
        bool duplicate)
    {
        var first = new SerializationTypeKey(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "first",
            1);
        var second = new SerializationTypeKey(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "second",
            2);
        SerializationTypeKey[] manifest = duplicate
            ? [first, first]
            : [second, first];
        using MemoryStream input = CreateHeader(SnapshotPayloadKind.World, manifest);
        using var reader = new BinaryReader(input, SerializationBinary.StrictUtf8, leaveOpen: true);
        var budget = new SerializationReadBudget(new SerializationReadLimits());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            if (manifestCountOnly)
                _ = PayloadFormat.ReadHeaderManifestCount(reader, budget);
            else
                _ = PayloadFormat.ReadHeader(reader, budget);
        });

        Assert.Contains(
            duplicate ? "Duplicate" : "canonical order",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EntityIdentityMode.Preserve, true)]
    [InlineData(EntityIdentityMode.Preserve, false)]
    [InlineData(EntityIdentityMode.Remap, true)]
    [InlineData(EntityIdentityMode.Remap, false)]
    public void WorldRead_RejectsDuplicateOrDescendingEntityManifestIndices(
        EntityIdentityMode identityMode,
        bool duplicate)
    {
        var firstKey = new SerializationTypeKey(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "first-tag",
            1);
        var secondKey = new SerializationTypeKey(
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            "second-tag",
            2);
        var registry = new SerializationRegistry()
            .RegisterTag<SerPlayerTag>(firstKey)
            .RegisterTag<SerEnemyTag>(secondKey);
        using var source = new World();
        Entity entity = source.CreateEntity();
        source.AddTag<SerPlayerTag>(entity);
        source.AddTag<SerEnemyTag>(entity);
        using var output = new MemoryStream();
        WorldSerializer.WriteWorld(
            output,
            source,
            registry,
            new SerializeOptions(Contract: SerializationContract.DurableSave));
        byte[] bytes = output.ToArray();
        (int firstIndexOffset, int secondIndexOffset) = FindTagManifestIndexOffsets(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(firstIndexOffset, sizeof(int)),
            duplicate ? 0 : 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(secondIndexOffset, sizeof(int)),
            0);

        using var input = new MemoryStream(bytes, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
        {
            using World loaded = WorldSerializer.ReadWorld(
                input,
                registry,
                new WorldLoadOptions(IdentityMode: identityMode));
        });

        Assert.Contains("duplicate or not canonical", error.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateComponentEnvelope(SerializationTypeKey key)
    {
        using MemoryStream stream = CreateHeader(SnapshotPayloadKind.Component, [key]);
        stream.Position = stream.Length;
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        writer.Write(0);
        writer.Write(1f);
        writer.Write(2f);
        writer.Write(sizeof(float) * 2);
        writer.Flush();
        return stream.ToArray();
    }

    private static MemoryStream CreateHeader(
        SnapshotPayloadKind kind,
        IReadOnlyList<SerializationTypeKey> manifest)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        writer.Write(PayloadFormat.Magic);
        writer.Write(PayloadFormat.Version);
        writer.Write((byte)SerializationContract.DurableSave);
        writer.Write((byte)kind);
        writer.Write(stackalloc byte[16]);
        writer.Write(manifest.Count);
        for (int i = 0; i < manifest.Count; i++)
            PayloadFormat.WriteTypeKey(writer, manifest[i]);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static (int First, int Second) FindTagManifestIndexOffsets(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        var budget = new SerializationReadBudget(new SerializationReadLimits());
        var (_, manifest, _) = PayloadFormat.ReadHeader(reader, budget);
        Assert.Equal(2, manifest.Length);
        _ = reader.ReadUInt32();
        int slotCount = reader.ReadInt32();
        for (int i = 0; i < slotCount; i++)
        {
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadByte();
        }
        Assert.Equal(1, reader.ReadInt32());
        var data = new DataReader(reader, budget);
        _ = data.ReadEntity();
        Assert.Equal(2, reader.ReadInt32());
        int first = checked((int)stream.Position);
        Assert.Equal(0, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        int second = checked((int)stream.Position);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(0, reader.ReadInt32());
        return (first, second);
    }
}
