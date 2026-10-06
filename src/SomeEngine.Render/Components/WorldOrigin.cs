using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Render.Components;

/// <summary>
/// Absolute origin of one Game world in engine world units. Transform positions, camera
/// positions and light positions are float offsets from this origin.
/// </summary>
public readonly record struct WorldOrigin(double X, double Y, double Z) : IComponent
{
    public double Dot(Vector3 direction) => X * direction.X + Y * direction.Y + Z * direction.Z;

    public void Split(out Vector3 high, out Vector3 low)
    {
        high = new((float)X, (float)Y, (float)Z);
        low = new((float)(X - high.X), (float)(Y - high.Y), (float)(Z - high.Z));
    }

    /// <summary>Translation from this origin into the reference origin's float coordinate space.</summary>
    public Vector3 RelativeTo(WorldOrigin reference) =>
        new((float)(X - reference.X), (float)(Y - reference.Y), (float)(Z - reference.Z));
}

/// <summary>World origin delivered at the same extraction boundary as mesh and light data.</summary>
public readonly record struct RenderWorldOrigin(WorldOrigin Value) : IComponent;
