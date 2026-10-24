using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

/// <summary>Explicit identity for a material type, independent of its C# namespace.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MaterialTypeAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public enum MaterialParameterKind : byte
{
    Texture = 1, Sampler = 2, Float = 3, Int = 4, UInt = 5, Bool = 6,
    Vector2 = 7, Vector3 = 8, Vector4 = 9, Matrix4x4 = 10,
}

/// <summary>A named material value. Texture properties receive a direct Texture separately.</summary>
public readonly record struct MaterialValue(string Name, MaterialParameterKind Kind)
{
    public SamplerDesc Sampler { get; init; }
    public float Float { get; init; }
    public int Int { get; init; }
    public uint UInt { get; init; }
    public bool Bool { get; init; }
    public Vector2 Vector2 { get; init; }
    public Vector3 Vector3 { get; init; }
    public Vector4 Vector4 { get; init; }
    public Matrix4x4 Matrix4x4 { get; init; }
}
