namespace SomeEngine.ECS.Serialization;

public readonly record struct SavedTypeKey(
    Guid Id,
    string Name,
    ulong FieldHash);
