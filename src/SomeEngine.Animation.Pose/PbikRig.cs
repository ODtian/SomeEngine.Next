using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Correction stiffness and preferred local Euler angles in radians, relative to the reference pose.</summary>
public readonly record struct PbikBoneSettings(int Bone, float PositionStiffness = 0, float RotationStiffness = 0,
    Vector3 PreferredAngles = default, float PreferredWeight = 0);

/// <summary>Local X/Y/Z Euler angle bounds in radians, relative to the reference pose.</summary>
public readonly record struct PbikJointLimit(int Bone, Vector3 Minimum, Vector3 Maximum);

/// <summary>A model-space effector position and its influence from the current animation position.</summary>
public readonly record struct PbikTarget(Vector3 Position, float Weight = 1);

/// <summary>Shared topology and constraints for the bodies connecting one root to all effectors.</summary>
public sealed class PbikRig
{
    private readonly int[] _effectors;
    private readonly PbikBoneSettings[] _settings;
    private readonly PbikJointLimit[] _limits;
    internal readonly PbikBody[] Bodies;
    internal readonly int[] BoneBodies, EffectorBodies;

    public Skeleton Skeleton { get; }
    public int Root { get; }
    public ReadOnlySpan<int> Effectors => _effectors;
    public ReadOnlySpan<PbikBoneSettings> Settings => _settings;
    public ReadOnlySpan<PbikJointLimit> Limits => _limits;
    public float Tolerance { get; }
    public int Iterations { get; }

    public PbikRig(Skeleton skeleton, int root, ReadOnlySpan<int> effectors,
        ReadOnlySpan<PbikBoneSettings> settings = default, ReadOnlySpan<PbikJointLimit> limits = default,
        float tolerance = .001f, int iterations = 128)
    {
        Skeleton = skeleton;
        Root = root;
        _effectors = effectors.ToArray();
        _settings = settings.ToArray();
        _limits = limits.ToArray();
        Tolerance = tolerance;
        Iterations = iterations;
        BoneBodies = new int[skeleton.BoneCount];
        Array.Fill(BoneBodies, -1);
        var affected = new bool[skeleton.BoneCount];
        foreach (int effector in effectors)
            for (int bone = effector; ; bone = skeleton.Parents[bone])
            {
                affected[bone] = true;
                if (bone == root) break;
            }
        var bodies = new List<PbikBody>();
        for (int bone = 0; bone < affected.Length; bone++)
            if (affected[bone])
            {
                BoneBodies[bone] = bodies.Count;
                bodies.Add(new(bone, bone == root ? -1 : BoneBodies[skeleton.Parents[bone]],
                    1, 1, skeleton.ReferencePose[bone].Rotation, 0, default, default, false));
            }
        Bodies = bodies.ToArray();
        foreach (PbikBoneSettings setting in settings)
        {
            int body = BoneBodies[setting.Bone];
            Bodies[body] = Bodies[body] with
            {
                PositionMobility = 1 - setting.PositionStiffness,
                RotationMobility = 1 - setting.RotationStiffness,
                PreferredRotation = skeleton.ReferencePose[setting.Bone].Rotation * PbikAngles.Rotation(setting.PreferredAngles),
                PreferredWeight = setting.PreferredWeight,
            };
        }
        foreach (PbikJointLimit limit in limits)
        {
            int body = BoneBodies[limit.Bone];
            Bodies[body] = Bodies[body] with { Minimum = limit.Minimum, Maximum = limit.Maximum, Limited = true };
        }
        EffectorBodies = new int[effectors.Length];
        for (int effector = 0; effector < effectors.Length; effector++)
            EffectorBodies[effector] = BoneBodies[effectors[effector]];
    }
}

internal readonly record struct PbikBody(int Bone, int Parent, float PositionMobility, float RotationMobility,
    Quaternion PreferredRotation, float PreferredWeight, Vector3 Minimum, Vector3 Maximum, bool Limited);

internal static class PbikAngles
{
    internal static Quaternion Rotation(Vector3 angles) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.Y) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X);

    internal static Vector3 Euler(Quaternion q) => new(
        MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)),
        MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1)),
        MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)));
}
