using System.Numerics;

namespace SomeEngine.Animation.Pose;

public enum BoneTransformProperty : byte { Translation, Rotation, Scale }
public enum ClipInterpolation : byte { Step, Linear, CubicSpline }

/// <summary>Source key times and values for one bone property. Cubic values retain in/value/out triples.</summary>
public sealed class RawClipChannel
{
    private readonly float[] _times;
    private readonly Vector4[] _values;

    public short Bone { get; }
    public BoneTransformProperty Property { get; }
    public ClipInterpolation Interpolation { get; }
    public ReadOnlySpan<float> Times => _times;
    public ReadOnlySpan<Vector4> Values => _values;

    public RawClipChannel(short bone, BoneTransformProperty property, ClipInterpolation interpolation,
        ReadOnlySpan<float> times, ReadOnlySpan<Vector4> values)
        : this(bone, property, interpolation, times.ToArray(), values.ToArray()) { }

    internal RawClipChannel(short bone, BoneTransformProperty property, ClipInterpolation interpolation,
        float[] times, Vector4[] values)
    {
        Bone = bone;
        Property = property;
        Interpolation = interpolation;
        _times = times;
        _values = values;
    }

    public Vector4 Sample(float time)
    {
        int key = Array.BinarySearch(_times, time);
        if (key >= 0) return KeyValue(key);
        int next = ~key;
        if (next == 0) return KeyValue(0);
        if (next == _times.Length) return KeyValue(next - 1);
        int previous = next - 1;
        Vector4 start = KeyValue(previous);
        if (Interpolation == ClipInterpolation.Step) return start;
        Vector4 end = KeyValue(next);
        float duration = _times[next] - _times[previous];
        float amount = (time - _times[previous]) / duration;
        if (Interpolation == ClipInterpolation.Linear)
        {
            if (Property != BoneTransformProperty.Rotation) return Vector4.Lerp(start, end, amount);
            Quaternion rotation = Quaternion.Slerp(new(start.X, start.Y, start.Z, start.W),
                new(end.X, end.Y, end.Z, end.W), amount);
            return new(rotation.X, rotation.Y, rotation.Z, rotation.W);
        }

        float square = amount * amount;
        float cube = square * amount;
        Vector4 value = (2 * cube - 3 * square + 1) * start
            + duration * (cube - 2 * square + amount) * _values[previous * 3 + 2]
            + (-2 * cube + 3 * square) * end
            + duration * (cube - square) * _values[next * 3];
        return Property == BoneTransformProperty.Rotation ? Vector4.Normalize(value) : value;
    }

    private Vector4 KeyValue(int key) => _values[Interpolation == ClipInterpolation.CubicSpline ? key * 3 + 1 : key];
}
