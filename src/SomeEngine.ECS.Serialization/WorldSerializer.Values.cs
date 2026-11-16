using SomeEngine.ECS.Storage;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

public readonly record struct SerializationReadOptions(
    SavePurpose? RequiredPurpose = null,
    ReadLimits? ReadLimits = null,
    MissingReferenceMode MissingReferenceMode = MissingReferenceMode.Throw);

public sealed partial class WorldSerializer
{
    public static void WriteComponent<T>(Stream output, in T value, SaveRegistry registry,
        SerializeOptions options = default) where T : struct
    {
        var serializer = new WorldSerializer(registry);
        int type = ComponentType<T>.TypeId;
        SavedType binding = registry.Columns.TryGetValue(type, out SavedColumn? column) ? column : registry.Sparse[type];
        serializer._used.Add(binding);
        var writer = new SaveWriter(output, options.ExternalReferences);
        serializer.WriteHeader(ref writer, options.Purpose, payloadKind: 1);
        long start = writer.Position;
        ((IStoredValueSerializer<T>)binding).WriteValue(ref writer, value);
        writer.WriteInt32(checked((int)(writer.Position - start)));
        writer.Flush();
    }

    public static T ReadComponent<T>(Stream input, SaveRegistry registry,
        SerializationReadOptions options = default) where T : struct
    {
        var reader = new SaveReader(input, options.ReadLimits);
        try
        {
            SavedType?[] types = new WorldSerializer(registry).ReadManifest(ref reader, options.RequiredPurpose, payloadKind: 1);
            if (types.Length != 1 || types[0] is not IStoredValueSerializer<T> serializer)
                throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch, "The saved component type differs from the requested type.");
            try
            {
                long start = reader.Position;
                T value = serializer.ReadValue(ref reader);
                long bytes = reader.Position - start;
                int length = reader.ReadInt32();
                if (bytes != length) throw new InvalidDataException("The component payload length differs from its encoded fields.");
                reader.Budget.PayloadLength(length);
                return value;
            }
            catch (EndOfStreamException error)
            {
                throw SerializationErrors.CreateDataException(SerializationError.InvalidData, "The component payload is truncated.", error);
            }
        }
        finally { reader.Dispose(); }
    }
}
