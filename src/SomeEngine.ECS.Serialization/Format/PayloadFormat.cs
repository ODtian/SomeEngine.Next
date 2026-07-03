namespace SomeEngine.ECS.Serialization;

internal static class PayloadFormat
{
    public const uint Magic = 0x53434553; // "SECS"
    public const ushort Version = 1;

    public static void WriteHeader(BinaryWriter writer, SnapshotPayloadKind kind, IReadOnlyList<SerializationTypeRuntime> manifest)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write((byte)0); // flags reserved
        writer.Write((byte)kind);
        writer.Write(manifest.Count);
        for (int i = 0; i < manifest.Count; i++)
            WriteTypeKey(writer, manifest[i].Entry.TypeKey);
    }

    public static (SnapshotPayloadKind Kind, SerializationTypeKey[] Manifest) ReadHeader(BinaryReader reader)
    {
        uint magic = reader.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException("Invalid SomeEngine.ECS serialization header magic.");

        ushort version = reader.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"Unsupported SomeEngine.ECS serialization format version {version}.");

        _ = reader.ReadByte(); // flags reserved
        var kind = (SnapshotPayloadKind)reader.ReadByte();
        int manifestCount = reader.ReadInt32();
        if (manifestCount < 0)
            throw new InvalidDataException("Negative serialization manifest count.");

        SerializationTypeKey[] manifest = new SerializationTypeKey[manifestCount];
        for (int i = 0; i < manifest.Length; i++)
            manifest[i] = ReadTypeKey(reader);

        return (kind, manifest);
    }

    public static void WriteTypeKey(BinaryWriter writer, SerializationTypeKey key)
    {
        var dataWriter = new DataWriter(writer);
        dataWriter.WriteGuid(key.StableId);
        writer.Write(key.StableName);
        writer.Write(key.SchemaHash);
    }

    public static SerializationTypeKey ReadTypeKey(BinaryReader reader)
    {
        var dataReader = new DataReader(reader);
        Guid stableId = dataReader.ReadGuid();
        string stableName = reader.ReadString();
        uint schemaHash = reader.ReadUInt32();
        return new SerializationTypeKey(stableId, stableName, schemaHash);
    }
}

