using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Read-only model-space bone transforms with their evaluation version and time.</summary>
public readonly ref struct ModelPose
{
    public ReadOnlySpan<Matrix4x4> Transforms { get; }
    public ulong Version { get; }
    public double Time { get; }

    internal ModelPose(ReadOnlySpan<Matrix4x4> transforms, ulong version, double time)
    {
        Transforms = transforms;
        Version = version;
        Time = time;
    }
}
