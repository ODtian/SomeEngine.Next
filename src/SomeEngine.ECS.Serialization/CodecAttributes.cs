namespace SomeEngine.ECS.Serialization;

/// <summary>Generates a field codec and a typed registry entry for a partial value.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateCodecAttribute : Attribute;

/// <summary>Selects the value codec used by one generated field or property.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class FieldCodecAttribute(Type codec) : Attribute
{ public Type Codec { get; } = codec; }
