using System.Numerics;
using System.Runtime.Intrinsics;

namespace SomeEngine.Animation.Pose;

/// <summary>Concrete TRS value operations and weighted loops over contiguous pose rows.</summary>
public readonly struct BoneTransformOps : IValueOps<BoneTransform>
{
    public static BoneTransform Lerp(in BoneTransform a, in BoneTransform b, float t) => new()
    {
        Translation = Vector3.Lerp(a.Translation, b.Translation, t),
        Rotation = Quaternion.Lerp(a.Rotation, b.Rotation, t),
        Scale = Vector3.Lerp(a.Scale, b.Scale, t),
    };

    public static BoneTransform Add(in BoneTransform a, in BoneTransform b) => new()
    {
        Translation = a.Translation + b.Translation, Rotation = a.Rotation + b.Rotation, Scale = a.Scale + b.Scale,
    };

    public static BoneTransform Subtract(in BoneTransform a, in BoneTransform b) => new()
    {
        Translation = a.Translation - b.Translation, Rotation = a.Rotation - b.Rotation, Scale = a.Scale - b.Scale,
    };

    public static BoneTransform Scale(in BoneTransform value, float scale) => new()
    {
        Translation = value.Translation * scale, Rotation = value.Rotation * scale, Scale = value.Scale * scale,
    };

    public static float Distance(in BoneTransform a, in BoneTransform b) => MathF.Sqrt(
        Vector3.DistanceSquared(a.Translation, b.Translation) + Vector3.DistanceSquared(a.Scale, b.Scale) +
        (a.Rotation - b.Rotation).LengthSquared());

    public static void Lerp<TOps>(ReadOnlySpan<BoneTransform> a, ReadOnlySpan<BoneTransform> b, float t,
        Span<BoneTransform> result) where TOps : struct, IValueOps<BoneTransform>
    {
        for (int bone = 0; bone < a.Length; bone++) result[bone] = Lerp(in a[bone], in b[bone], t);
    }

    public static void Lerp<TOps>(ReadOnlySpan<BoneTransform> a, ReadOnlySpan<BoneTransform> b,
        ReadOnlySpan<float> progress, Span<BoneTransform> result) where TOps : struct, IValueOps<BoneTransform>
    {
        for (int bone = 0; bone < a.Length; bone++) result[bone] = Lerp(in a[bone], in b[bone], progress[bone]);
    }

    /// <summary>Blends pose rows with nonnegative weights and a positive total, normalizing hemisphere-aligned rotations.</summary>
    public static void Weighted(ReadOnlySpan<BoneTransform> poses, int boneCount, ReadOnlySpan<float> weights,
        Span<BoneTransform> result)
    {
        float total = 0;
        foreach (float weight in weights) total += weight;
        if (weights.Length == 2)
        {
            ValueOps<BoneTransform, BoneTransformOps>.Lerp(poses[..boneCount], poses.Slice(boneCount, boneCount),
                weights[1] / total, result);
            return;
        }
        float inverseTotal = 1 / total;
        int first = 0;
        while (weights[first] == 0) first++;
        for (int bone = 0; bone < boneCount; bone++)
        {
            Quaternion reference = poses[first * boneCount + bone].Rotation;
            Vector3 translation = Vector3.Zero, scale = Vector3.Zero;
            Vector128<float> rotation = Vector128<float>.Zero;
            for (int source = 0; source < weights.Length; source++)
            {
                float weight = weights[source] * inverseTotal;
                if (weight == 0) continue;
                ref readonly BoneTransform value = ref poses[source * boneCount + bone];
                translation += value.Translation * weight;
                scale += value.Scale * weight;
                Quaternion q = value.Rotation;
                if (Quaternion.Dot(reference, q) < 0) weight = -weight;
                rotation += Vector128.Create(q.X, q.Y, q.Z, q.W) * weight;
            }
            result[bone] = new BoneTransform
            {
                Translation = translation, Scale = scale,
                Rotation = Quaternion.Normalize(new Quaternion(rotation.GetElement(0), rotation.GetElement(1),
                    rotation.GetElement(2), rotation.GetElement(3))),
            };
        }
    }
}
