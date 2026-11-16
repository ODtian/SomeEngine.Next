namespace SomeEngine.ECS.Serialization;

/// <summary>Generates a field serializer and a typed registry entry for a partial value.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateSerializerAttribute : Attribute;

/// <summary>Selects the value serializer used by one generated field or property.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SerializeWithAttribute(Type serializer) : Attribute
{ public Type Serializer { get; } = serializer; }
