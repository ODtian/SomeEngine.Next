namespace SomeEngine.ECS.Serialization;

/// <summary>How a registered type's values are stored.</summary>
public enum ValueEncoding : byte { Missing, MemoryCopy, Serializer, PackedMemoryCopy }
public enum SerializationValueKind : byte { Component, Tag, Shared, Buffer, Sparse, Resource }

public readonly record struct SerializationTypeEntry(
    SavedTypeKey TypeKey,
    SerializationValueKind Kind,
    ValueEncoding ValueEncoding);

internal static class SavedTypeDescription
{
    internal static SerializationTypeEntry Describe(this SavedType type) => new(
        type.ValueEncoding == ValueEncoding.MemoryCopy ? type.CheckpointKey : type.Key,
        type.Storage switch
        {
            SavedStorage.Tag => SerializationValueKind.Tag,
            SavedStorage.Shared => SerializationValueKind.Shared,
            SavedStorage.Buffer => SerializationValueKind.Buffer,
            SavedStorage.Sparse => SerializationValueKind.Sparse,
            SavedStorage.Resource => SerializationValueKind.Resource,
            _ => SerializationValueKind.Component,
        }, type.ValueEncoding);
}
