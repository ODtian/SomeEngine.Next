// Weighted descendant pin propagation adapted from Everything-Will-Be-IK.
// Copyright (c) 2016 Eron Gjoni. MIT license: external/ewbik/LICENSE.
using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>An own-bone pin, descendant influence falloff and model-space orientation lever length.</summary>
public readonly record struct EwbikEffector(int Bone, float DepthFalloff = 1, float AxisLength = 1);
/// <summary>Multiplier for the joint's angular step limit. Zero retains the hard-limited source rotation.</summary>
public readonly record struct EwbikBoneSettings(int Bone, float DampingScale = 1);
/// <summary>Angular step limit and orientation tolerance in radians, position tolerance in model units.</summary>
public sealed record EwbikSettings(float Damping = .35f, int Iterations = 128, float PositionTolerance = .001f, float OrientationTolerance = .001f);
/// <summary>A model-space target with nonnegative overall and X/Y/Z orientation priorities.</summary>
public readonly record struct EwbikTarget(Vector3 Position, Quaternion Orientation, float Weight = 1, Vector3 AxisPriorities = default);

/// <summary>Shared ancestor/pin connections and descendant model-update ranges.</summary>
public sealed class EwbikRig
{
    private readonly EwbikEffector[] _effectors;
    private readonly EwbikBoneSettings[] _boneSettings;
    private readonly EwbikConstraint[] _constraints;
    public Skeleton Skeleton { get; }
    public int Root { get; }
    public EwbikSettings Settings { get; }
    public ReadOnlySpan<EwbikEffector> Effectors => _effectors;
    public ReadOnlySpan<EwbikBoneSettings> BoneSettings => _boneSettings;
    public ReadOnlySpan<EwbikConstraint> Constraints => _constraints;
    internal readonly short[] Bones;
    internal readonly EwbikJoint[] Joints;
    internal readonly EwbikInfluence[] Influences;
    internal readonly short[] Descendants;
    internal readonly int[] BoneConstraints;

    public EwbikRig(Skeleton skeleton, int root, ReadOnlySpan<EwbikEffector> effectors,
        ReadOnlySpan<EwbikBoneSettings> bones = default, EwbikSettings? settings = null,
        ReadOnlySpan<EwbikConstraint> constraints = default)
    {
        Skeleton = skeleton;
        Root = root;
        Settings = settings ?? new();
        _effectors = effectors.ToArray();
        _boneSettings = bones.ToArray();
        _constraints = constraints.ToArray();
        var affected = new bool[skeleton.BoneCount];
        BoneConstraints = new int[skeleton.BoneCount];
        Array.Fill(BoneConstraints, -1);
        for (int index = 0; index < constraints.Length; index++)
        {
            int constrained = constraints[index].Bone;
            BoneConstraints[constrained] = index;
            for (int bone = constrained; ; bone = skeleton.Parents[bone])
            {
                affected[bone] = true;
                if (bone == root) break;
            }
        }
        foreach (EwbikEffector effector in effectors)
            for (int bone = effector.Bone; ; bone = skeleton.Parents[bone])
            {
                affected[bone] = true;
                if (bone == root) break;
            }
        var selected = new List<short>();
        var joints = new List<EwbikJoint>();
        var influences = new List<EwbikInfluence>();
        var descendants = new List<short>();
        for (int bone = 0; bone < affected.Length; bone++)
        {
            if (!affected[bone]) continue;
            selected.Add((short)bone);
            int influenceStart = influences.Count, descendantStart = descendants.Count;
            for (int effector = 0; effector < effectors.Length; effector++)
            {
                float falloff = 1;
                for (int current = effectors[effector].Bone; current >= 0; current = skeleton.Parents[current])
                {
                    if (current == bone) { influences.Add(new(effector, falloff)); break; }
                    int parent = skeleton.Parents[current];
                    foreach (EwbikEffector pin in effectors)
                        if (pin.Bone == parent) falloff *= pin.DepthFalloff;
                }
            }
            for (int child = bone; child < skeleton.BoneCount; child++)
                for (int current = child; current >= 0; current = skeleton.Parents[current])
                    if (current == bone) { descendants.Add((short)child); break; }
            float damping = 1;
            foreach (EwbikBoneSettings setting in bones)
                if (setting.Bone == bone) damping = setting.DampingScale;
            joints.Add(new(bone, influenceStart, influences.Count - influenceStart,
                descendantStart, descendants.Count - descendantStart, damping));
        }
        Bones = selected.ToArray();
        Joints = joints.ToArray();
        Influences = influences.ToArray();
        Descendants = descendants.ToArray();
    }
}

internal readonly record struct EwbikJoint(int Bone, int InfluenceStart, int InfluenceCount,
    int DescendantStart, int DescendantCount, float DampingScale);
internal readonly record struct EwbikInfluence(int Effector, float Weight);
