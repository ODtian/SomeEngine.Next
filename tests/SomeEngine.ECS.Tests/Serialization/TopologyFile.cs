using System.Buffers.Binary;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

// Locates fields in the small integer-and-topology fixtures emitted by the public serializer.
internal sealed class TopologyFile
{
    internal readonly record struct TypeField(int Index, byte Storage, int NameOffset, int NameLength);
    internal readonly record struct ValueField(int TypeIndex, int EntityOffset, int ValueOffset, int ValueLength);
    internal readonly record struct HeadField(int EntityId, bool Incoming, int MembersOffset, int Count);

    internal byte[] Bytes { get; }
    internal List<TypeField> Types { get; } = [];
    internal List<int> EntityFields { get; } = [];
    internal List<ValueField> Values { get; } = [];
    internal List<HeadField> Heads { get; } = [];
    internal List<int> ServiceIndices { get; } = [];
    internal int ServiceCountOffset { get; private set; }

    internal static TopologyFile Save(World world, SerializationRegistry registry)
    {
        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(world, output, new SerializeOptions(Purpose: SavePurpose.DurableSave), CheckpointProgress.Preserve);
        return new(output.ToArray());
    }

    private TopologyFile(byte[] bytes)
    {
        Bytes = bytes;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, SaveBytes.Utf8);
        stream.Position = 24;
        int count = reader.ReadInt32();
        var widths = new int[count];
        for (int i = 0; i < count; i++)
        {
            reader.ReadBytes(16);
            int nameLength = reader.ReadInt32();
            int nameOffset = checked((int)stream.Position);
            reader.ReadChars(nameLength);
            reader.ReadUInt64();
            byte storage = reader.ReadByte();
            widths[i] = reader.ReadInt32();
            Types.Add(new(i, storage, nameOffset, nameLength));
        }
        reader.ReadByte(); reader.ReadUInt64();
        int used = reader.ReadInt32(); reader.ReadInt32();
        int free = reader.ReadInt32();
        reader.ReadBytes((used + free) * sizeof(int));
        Assert.Equal(0, reader.ReadInt32()); // These fixtures have no shared values.
        int tables = reader.ReadInt32();
        for (int table = 0; table < tables; table++)
        {
            int columns = reader.ReadInt32();
            var types = new int[columns];
            for (int i = 0; i < columns; i++) types[i] = reader.ReadInt32();
            int groups = reader.ReadInt32(); reader.ReadBytes(groups * 8);
            int chunks = reader.ReadInt32();
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                int rows = reader.ReadInt32();
                for (int row = 0; row < rows; row++)
                { EntityFields.Add(checked((int)stream.Position)); reader.ReadBytes(8); }
                foreach (int type in types)
                {
                    SkipMask(reader, rows);
                    byte storage = Types[type].Storage;
                    if (storage == 0)
                    {
                        int width = widths[type];
                        reader.ReadBytes(rows * (width == 0 ? sizeof(int) : width));
                        if (width == 0) reader.ReadInt32();
                    }
                    SkipMask(reader, rows);
                    byte changes = reader.ReadByte();
                    if ((changes & 1) != 0) reader.ReadBytes(rows * ((changes & 2) != 0 ? 16 : 8));
                }
            }
        }
        Assert.Equal(0, reader.ReadInt32()); // sparse values
        Assert.Equal(0, reader.ReadInt32()); // resources
        int topology = reader.ReadInt32();
        for (int type = 0; type < topology; type++)
        {
            int index = reader.ReadInt32();
            int rows = reader.ReadInt32();
            for (int row = 0; row < rows; row++)
            {
                int entity = checked((int)stream.Position); reader.ReadBytes(8);
                int value = checked((int)stream.Position);
                switch (Types[index].Storage)
                {
                    case 6: reader.ReadBytes(8); break;
                    case 7: reader.ReadBytes(16); break;
                    case 8:
                        int children = reader.ReadInt32(); reader.ReadBoolean(); reader.ReadBytes(children * 8); break;
                }
                Values.Add(new(index, entity, value, checked((int)stream.Position - value)));
            }
            reader.ReadInt32();
        }
        reader.ReadUInt64();
        int facts = reader.ReadInt32(); reader.ReadBytes(facts * 16);
        int readers = reader.ReadInt32(); reader.ReadBytes(readers * 24);
        ServiceCountOffset = checked((int)stream.Position);
        int services = reader.ReadInt32();
        for (int service = 0; service < services; service++)
        {
            ServiceIndices.Add(checked((int)stream.Position));
            int index = reader.ReadInt32();
            if (Types[index].Storage == 6) SkipHierarchy(reader);
            else SkipRelation(reader);
        }
    }

    internal void Read(SerializationRegistry registry)
    {
        using var input = new MemoryStream(Bytes, writable: false);
        using World loaded = WorldSerializer.ReadWorld(input, registry, new WorldLoadOptions(RequiredPurpose: SavePurpose.DurableSave));
    }

    internal void CopyEntity(int source, int target) => Bytes.AsSpan(source, 8).CopyTo(Bytes.AsSpan(target, 8));
    internal void SetInt(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset, 4), value);

    private static void SkipMask(BinaryReader reader, int rows)
    {
        if (reader.ReadByte() != 0) reader.ReadBytes(((rows + 63) / 64) * 8);
    }

    private static void SkipChildren(BinaryReader reader)
    {
        int count = reader.ReadInt32(); reader.ReadBoolean(); reader.ReadBytes(count * 8);
    }

    private static void SkipHierarchy(BinaryReader reader)
    {
        reader.ReadBytes(18);
        int applied = reader.ReadInt32(); reader.ReadBytes(applied * 16);
        int retired = reader.ReadInt32(); reader.ReadBytes(retired * 24);
        int children = reader.ReadInt32();
        for (int i = 0; i < children; i++) { reader.ReadBytes(8); SkipChildren(reader); }
        int future = reader.ReadInt32();
        for (int i = 0; i < future; i++) { reader.ReadBytes(8); int count = reader.ReadInt32(); reader.ReadBytes(count * 8); }
        int ordered = reader.ReadInt32(); reader.ReadBytes(ordered * 8);
    }

    private void SkipRelation(BinaryReader reader)
    {
        reader.ReadBytes(21);
        ReadHeads(reader, incoming: false);
        ReadHeads(reader, incoming: true);
        int applied = reader.ReadInt32(); reader.ReadBytes(applied * 28);
        int retired = reader.ReadInt32(); reader.ReadBytes(retired * 36);
        int heads = reader.ReadInt32();
        for (int i = 0; i < heads; i++) { reader.ReadBytes(9); SkipHead(reader); }
        int future = reader.ReadInt32();
        for (int i = 0; i < future; i++) { reader.ReadBytes(9); int count = reader.ReadInt32(); reader.ReadBytes(count * 8); }
    }

    private void ReadHeads(BinaryReader reader, bool incoming)
    {
        int count = reader.ReadInt32();
        for (int id = 0; id < count; id++)
        {
            reader.ReadInt32(); int members = reader.ReadInt32(); reader.ReadBoolean();
            int offset = checked((int)reader.BaseStream.Position);
            Heads.Add(new(id, incoming, offset, members));
            reader.ReadBytes(members * 16);
        }
    }

    private static void SkipHead(BinaryReader reader)
    {
        reader.ReadInt32(); int count = reader.ReadInt32(); reader.ReadBoolean(); reader.ReadBytes(count * 16);
    }
}
