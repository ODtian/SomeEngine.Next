using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The caller's transform in node-local coordinates and opacity multiplier.</summary>
public struct ElementTransform : IComponent
{
    public Matrix3x2 Matrix;
    public float Alpha;
    public static ElementTransform Identity => new() { Matrix = Matrix3x2.Identity, Alpha = 1 };
}
