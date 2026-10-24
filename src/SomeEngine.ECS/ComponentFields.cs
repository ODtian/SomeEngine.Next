namespace SomeEngine.ECS;

/// <summary>Generates typed access to the public unmanaged leaves of a component.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class GenerateFieldsAttribute : Attribute;

/// <summary>Adds a nested field path or a hand-written field accessor to the component declaration.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class ComponentFieldAttribute : Attribute
{
    public ComponentFieldAttribute(string path) => Path = path;
    public ComponentFieldAttribute(Type access, string name) => (Access, Name) = (access, name);
    public string? Path { get; }
    public Type? Access { get; }
    public string? Name { get; set; }
}
