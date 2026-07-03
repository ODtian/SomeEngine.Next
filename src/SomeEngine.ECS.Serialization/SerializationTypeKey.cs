namespace SomeEngine.ECS.Serialization;

public readonly record struct SerializationTypeKey(
    Guid StableId,
    string StableName,
    uint SchemaHash);

