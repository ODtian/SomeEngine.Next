using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Navigation;

/// <summary>Actual movement feedback. Heading uses <see cref="NavigationHeading"/>.</summary>
public readonly record struct NavigationMotion(Vector3 Position, Vector3 Velocity, float Heading, bool Grounded);

public interface INavigationMotionReader<TMotion> where TMotion : unmanaged, IComponent
{
    static abstract NavigationMotion Read(in TMotion motion);
}

/// <summary>XZ heading in radians: zero points along +Z; positive rotation turns toward +X.</summary>
public static class NavigationHeading
{
    public static float FromDirection(Vector3 direction) => MathF.Atan2(direction.X, direction.Z);

    public static Vector3 Direction(float heading) => new(MathF.Sin(heading), 0, MathF.Cos(heading));

    public static float Difference(float target, float current) =>
        MathF.IEEERemainder(target - current, 2 * MathF.PI);
}
