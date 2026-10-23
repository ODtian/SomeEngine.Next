using System.Numerics;

namespace SomeEngine.Animation.Pose;

public readonly record struct MotionWarpWindow(float Start, float End);

/// <summary>A shared source path and the interval that aligns it with an interaction target.</summary>
public sealed class RootMotionWarp
{
    public RootMotionPath Source { get; }
    public MotionWarpWindow Window { get; }
    internal Matrix4x4 InverseEnd { get; }

    public RootMotionWarp(RootMotionPath source, MotionWarpWindow window)
    {
        Source = source;
        Window = window;
        Matrix4x4.Invert(source.Sample(window.End), out Matrix4x4 inverseEnd);
        InverseEnd = inverseEnd;
    }

    public MotionWarpInput Bind(Matrix4x4 start, Matrix4x4 target) => new(this, start, target);
}

/// <summary>One character's declared start, interaction target and pure corrected trajectory.</summary>
public readonly struct MotionWarpInput
{
    private readonly Vector3 _translation;
    private readonly Quaternion _rotation;

    public RootMotionWarp Warp { get; }
    public Matrix4x4 Start { get; }
    public Matrix4x4 Target { get; }

    internal MotionWarpInput(RootMotionWarp warp, Matrix4x4 start, Matrix4x4 target)
    {
        Warp = warp;
        Start = start;
        Target = target;
        Matrix4x4.Invert(start, out Matrix4x4 inverseStart);
        Matrix4x4 correction = warp.InverseEnd * target * inverseStart;
        Matrix4x4.Decompose(correction, out _, out _rotation, out _translation);
    }

    /// <summary>Cumulative actor-local movement, sampled independently of live playback history.</summary>
    public Matrix4x4 Sample(float time)
    {
        float amount = Math.Clamp((time - Warp.Window.Start) / (Warp.Window.End - Warp.Window.Start), 0, 1);
        amount = amount * amount * (3 - 2 * amount);
        Matrix4x4 correction = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(Quaternion.Identity, _rotation, amount))
            * Matrix4x4.CreateTranslation(_translation * amount);
        return Warp.Source.Sample(time) * correction;
    }

    public Matrix4x4 SampleWorld(float time) => Sample(time) * Start;
}
