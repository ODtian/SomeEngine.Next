using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The temporary canvas-coordinate transform and opacity used to continue a FLIP transition.</summary>
public struct PresentationOffset : IComponent
{
    public Matrix3x2 Matrix;
    public float Alpha;
    public static PresentationOffset Identity => new() { Matrix = Matrix3x2.Identity, Alpha = 1 };
}

public static class PresentationChannels
{
    public static readonly Channel<Matrix3x2> Matrix = Channel<Matrix3x2>.Of<PresentationOffset>(
        static (ref PresentationOffset offset) => ref offset.Matrix);
    public static readonly Channel<float> Alpha = Channel<float>.Of<PresentationOffset>(
        static (ref PresentationOffset offset) => ref offset.Alpha);
}
