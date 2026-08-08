namespace SomeEngine.ECS.Serialization;

[AttributeUsage(AttributeTargets.Struct)]
public sealed class SerializableComponentAttribute : Attribute
{
    public Guid StableId { get; }
    public uint SchemaVersion { get; init; } = 1;
    public uint CodecVersion { get; init; } = 1;

    public SerializableComponentAttribute(string stableId)
    {
        StableId = Guid.Parse(stableId);
    }
}

/// <summary>
/// Gives a field a durable identity that survives CLR/source renames. If omitted, the source
/// field name is used and renaming the field intentionally changes the schema fingerprint.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializedFieldAttribute : Attribute
{
    public string StableId { get; }

    public SerializedFieldAttribute(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        StableId = stableId;
    }
}

