using System.Text;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

// Malformed inputs are authored in the documented v4 wire format and read through public APIs.
internal static class SaveBytes
{
    internal static UTF8Encoding Utf8 { get; } = new(false, true);

    internal static void WriteTypeKey(BinaryWriter writer, SerializationTypeKey key)
    {
        Span<byte> id = stackalloc byte[16];
        BinaryPrimitiveEncoding.WriteGuid(id, key.StableId);
        writer.Write(id);
        WriteString(writer, key.StableName);
        writer.Write(key.SchemaFingerprint);
    }

    internal static void WriteString(BinaryWriter writer, string? value)
    {
        writer.Write(value?.Length ?? -1);
        if (value is not null)
            writer.Write(Utf8.GetBytes(value));
    }

    internal static string? ReadString(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count == -1 ? null : new string(reader.ReadChars(count));
    }

    internal static void WriteEntity(BinaryWriter writer, Entity entity)
    {
        writer.Write(entity.Index);
        writer.Write(entity.Generation);
    }

    internal static void SkipHeader(BinaryReader reader)
    {
        reader.ReadUInt32();
        reader.ReadUInt16();
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadBytes(16);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            reader.ReadBytes(16);
            ReadString(reader);
            reader.ReadUInt64();
        }
    }
}
