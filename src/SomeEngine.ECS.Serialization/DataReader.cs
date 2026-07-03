using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

public ref struct DataReader
{
    private readonly BinaryReader _reader;

    internal DataReader(BinaryReader reader)
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
    public Guid ReadGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        ReadExactBytes(_reader, bytes, "GUID payload");
        return new Guid(bytes);
    }

    public string? ReadString()
    {
        bool hasValue = _reader.ReadBoolean();
        return hasValue ? _reader.ReadString() : null;
    }

    public Entity ReadEntity()
    {
        return new Entity(_reader.ReadInt32(), _reader.ReadInt32());
    }

    public ExternalReferenceKey ReadExternalReference() => new(ReadGuid());

    internal void ReadRawBytes(Span<byte> destination)
    {
        ReadExactBytes(_reader, destination, "raw byte payload");
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

