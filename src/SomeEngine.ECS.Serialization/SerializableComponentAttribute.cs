namespace SomeEngine.ECS.Serialization;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class SerializableComponentAttribute : Attribute
{
    public Guid StableId { get; }
    public uint SchemaHash { get; init; }

    public SerializableComponentAttribute(string stableId)
    {
        StableId = Guid.Parse(stableId);
    }
}

