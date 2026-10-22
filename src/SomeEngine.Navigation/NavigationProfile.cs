using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>Completed shape and movement limits shared by agents and navigation representations.</summary>
public sealed class NavigationProfile(
    ReadOnlyMemory<Vector2> footprint,
    float height,
    float maximumSlopeRadians = MathF.PI / 4,
    float maximumStepHeight = 0.3f,
    float maximumSpeed = 3f,
    float maximumAcceleration = 8f,
    float maximumAngularSpeed = MathF.PI,
    ulong capabilities = ulong.MaxValue)
{
    public NavigationProfile(Vector2[] footprint, float height,
        float maximumSlopeRadians = MathF.PI / 4, float maximumStepHeight = 0.3f,
        float maximumSpeed = 3f, float maximumAcceleration = 8f,
        float maximumAngularSpeed = MathF.PI, ulong capabilities = ulong.MaxValue)
        : this((ReadOnlyMemory<Vector2>)footprint, height, maximumSlopeRadians, maximumStepHeight,
            maximumSpeed, maximumAcceleration, maximumAngularSpeed, capabilities) { }

    public ReadOnlyMemory<Vector2> Footprint { get; } = footprint;
    public float Height { get; } = height;
    public float MaximumSlopeRadians { get; } = maximumSlopeRadians;
    public float MaximumStepHeight { get; } = maximumStepHeight;
    public float MaximumSpeed { get; } = maximumSpeed;
    public float MaximumAcceleration { get; } = maximumAcceleration;
    public float MaximumAngularSpeed { get; } = maximumAngularSpeed;
    public ulong Capabilities { get; } = capabilities;
}
