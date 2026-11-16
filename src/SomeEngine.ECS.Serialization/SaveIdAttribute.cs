namespace SomeEngine.ECS.Serialization;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class SaveIdAttribute : Attribute
{
    public Guid Id { get; }
    public uint SchemaVersion { get; init; } = 1;
    public uint SerializerVersion { get; init; } = 1;

    public SaveIdAttribute(string savedId)
    {
        Id = Guid.Parse(savedId);
    }
}

/// <summary>
/// The name a field is saved under, so the field can be renamed in source without changing saves. If omitted,
/// the source field name is used and renaming the field changes the field hash.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SaveNameAttribute : Attribute
{
    public string Name { get; }

    public SaveNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }
}
