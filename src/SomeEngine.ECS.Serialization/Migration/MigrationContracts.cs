namespace SomeEngine.ECS.Serialization;

public interface IMigrationStep
{
    SerializationTypeKey From { get; }
    SerializationTypeKey To { get; }
    void Migrate(ref MigrationReader reader, ref MigrationWriter writer);
}

public ref struct MigrationReader
{
    private DataReader _reader;

    internal MigrationReader(DataReader reader)
    {
        _reader = reader;
    }

    public bool ReadBoolean() => _reader.ReadBoolean();
    public byte ReadByte() => _reader.ReadByte();
    public int ReadInt32() => _reader.ReadInt32();
    public uint ReadUInt32() => _reader.ReadUInt32();
    public long ReadInt64() => _reader.ReadInt64();
    public float ReadSingle() => _reader.ReadSingle();
    public double ReadDouble() => _reader.ReadDouble();
    public Guid ReadGuid() => _reader.ReadGuid();
    public string? ReadString() => _reader.ReadString();
    public global::SomeEngine.ECS.Entities.Entity ReadEntity() => _reader.ReadEntity();
    public ExternalReferenceKey ReadExternalReference() => _reader.ReadExternalReference();
}

public ref struct MigrationWriter
{
    private DataWriter _writer;

    internal MigrationWriter(DataWriter writer)
    {
        _writer = writer;
    }

    public void WriteBoolean(bool value) => _writer.WriteBoolean(value);
    public void WriteByte(byte value) => _writer.WriteByte(value);
    public void WriteInt32(int value) => _writer.WriteInt32(value);
    public void WriteUInt32(uint value) => _writer.WriteUInt32(value);
    public void WriteInt64(long value) => _writer.WriteInt64(value);
    public void WriteSingle(float value) => _writer.WriteSingle(value);
    public void WriteDouble(double value) => _writer.WriteDouble(value);
    public void WriteGuid(Guid value) => _writer.WriteGuid(value);
    public void WriteString(string? value) => _writer.WriteString(value);
    public void WriteEntity(global::SomeEngine.ECS.Entities.Entity value) => _writer.WriteEntity(value);
    public void WriteExternalReference(ExternalReferenceKey value) => _writer.WriteExternalReference(value);
}

