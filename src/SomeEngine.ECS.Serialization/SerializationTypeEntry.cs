using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Serialization;

public enum ComponentCodecKind : byte
{
    Missing,
    Raw,
    Custom,
}

public enum SerializationValueKind : byte
{
    Component,
    Tag,
    Shared,
    Buffer,
    Sparse,
    Relation,
}

public readonly struct SerializationTypeEntry
{
    public SerializationTypeKey TypeKey { get; }
    public int RuntimeComponentId { get; }
    public StoragePath Storage { get; }
    public SerializationValueKind Kind { get; }
    public ComponentCodecKind CodecKind { get; }
    public bool ContainsReferences { get; }
    public bool ContainsEntityReferences { get; }

    internal SerializationTypeEntry(
        SerializationTypeKey typeKey,
        int runtimeComponentId,
        StoragePath storage,
        SerializationValueKind kind,
        ComponentCodecKind codecKind,
        bool containsReferences,
        bool containsEntityReferences)
    {
        TypeKey = typeKey;
        RuntimeComponentId = runtimeComponentId;
        Storage = storage;
        Kind = kind;
        CodecKind = codecKind;
        ContainsReferences = containsReferences;
        ContainsEntityReferences = containsEntityReferences;
    }
}

