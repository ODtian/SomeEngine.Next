namespace SomeEngine.ECS.Serialization;

public enum ComponentCodecKind : byte { Missing, Raw, Custom, Durable, RawDurable }
public enum SerializationValueKind : byte { Component, Tag, Shared, Buffer, Sparse, Resource }

public readonly record struct SerializationTypeEntry(
    SerializationTypeKey TypeKey,
    SerializationValueKind Kind,
    ComponentCodecKind CodecKind);

internal static class SavedTypeDescription
{
    internal static SerializationTypeEntry Describe(this SavedType type) => new(
        type.CodecKind == ComponentCodecKind.Raw ? type.CheckpointKey : type.Key,
        type.Storage switch
        {
            SavedStorage.Tag => SerializationValueKind.Tag,
            SavedStorage.Shared => SerializationValueKind.Shared,
            SavedStorage.Buffer => SerializationValueKind.Buffer,
            SavedStorage.Sparse => SerializationValueKind.Sparse,
            SavedStorage.Resource => SerializationValueKind.Resource,
            _ => SerializationValueKind.Component,
        }, type.CodecKind);
}
