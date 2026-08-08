using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Serialization;

public enum ComponentCodecKind : byte
{
    Missing,
    Raw,
    Custom,
    Canonical,
    RawCanonical,
}

public enum SerializationValueKind : byte
{
    Component,
    Tag,
    Shared,
    Buffer,
    Sparse,
}

public enum SerializationSchemaSource : byte
{
    RuntimeDerived,
    Explicit,
}

public readonly struct SerializationTypeEntry
{
    public SerializationTypeKey TypeKey { get; }
    public int RuntimeComponentId { get; }
    public StoragePath Storage { get; }
    public SerializationValueKind Kind { get; }
    public ComponentCodecKind CodecKind { get; }
    public SerializationSchemaSource SchemaSource { get; }
    public bool ContainsReferences { get; }
    public bool ContainsEntityReferences { get; }

    internal SerializationTypeEntry(
        SerializationTypeKey typeKey,
        int runtimeComponentId,
        StoragePath storage,
        SerializationValueKind kind,
        ComponentCodecKind codecKind,
        SerializationSchemaSource schemaSource,
        bool containsReferences,
        bool containsEntityReferences)
    {
        TypeKey = typeKey;
        RuntimeComponentId = runtimeComponentId;
        Storage = storage;
        Kind = kind;
        CodecKind = codecKind;
        SchemaSource = schemaSource;
        ContainsReferences = containsReferences;
        ContainsEntityReferences = containsEntityReferences;
    }
}

