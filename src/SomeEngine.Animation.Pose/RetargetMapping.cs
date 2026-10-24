using System.Numerics;
using System.Collections.Immutable;

namespace SomeEngine.Animation.Pose;

/// <summary>Own root indices, reference model axes and source-axis translation scale.</summary>
public readonly record struct RetargetRoot(int Source, int Target, Quaternion SourceAxes,
    Quaternion TargetAxes, Vector3 TranslationScale);

/// <summary>Ordered connected own-bone chains and reference model axes for their motion.</summary>
public sealed class RetargetChain(ReadOnlySpan<short> source, ReadOnlySpan<short> target,
    Quaternion sourceAxes, Quaternion targetAxes, float GoalScale = 1, float GoalWeight = 1, Vector3 GoalOffset = default)
{
    private readonly short[] _source = source.ToArray(), _target = target.ToArray();
    public ReadOnlySpan<short> Source => _source;
    public ReadOnlySpan<short> Target => _target;
    public Quaternion SourceAxes { get; } = sourceAxes;
    public Quaternion TargetAxes { get; } = targetAxes;
    public float GoalScale { get; } = GoalScale;
    public float GoalWeight { get; } = GoalWeight;
    public Vector3 GoalOffset { get; } = GoalOffset;
}

/// <summary>Shared reference frames, normalized chain correspondence and target IK settings.</summary>
public sealed class RetargetMapping
{
    private readonly RetargetChain[] _chains;
    public Skeleton Source { get; }
    public Skeleton Target { get; }
    public RetargetRoot Root { get; }
    public ReadOnlySpan<RetargetChain> Chains => _chains;
    public PbikRig Correction { get; }
    internal readonly Matrix4x4[] SourceReference, TargetReference;
    internal readonly RetargetBone[] Bones;
    internal readonly RetargetEnd[] Ends;
    internal readonly short[] SourceBones, TargetBones;
    internal readonly Quaternion RootAlignment;

    public RetargetMapping(Skeleton source, Skeleton target, RetargetRoot root, ReadOnlySpan<RetargetChain> chains,
        ReadOnlySpan<PbikBoneSettings> settings = default, ReadOnlySpan<PbikJointLimit> limits = default,
        float tolerance = .001f, int iterations = 128)
    {
        Source = source;
        Target = target;
        Root = root;
        _chains = chains.ToArray();
        SourceReference = ReferenceModels(source);
        TargetReference = ReferenceModels(target);
        RootAlignment = Quaternion.Normalize(root.TargetAxes * Quaternion.Conjugate(root.SourceAxes));
        Bones = new RetargetBone[target.BoneCount];
        Array.Fill(Bones, new(-1, -1, 0, default, default, default));
        Ends = new RetargetEnd[chains.Length];
        var required = new bool[source.BoneCount];
        required[root.Source] = true;
        var effectors = new int[chains.Length];
        for (int chainIndex = 0; chainIndex < chains.Length; chainIndex++)
        {
            RetargetChain chain = chains[chainIndex];
            float[] sourceDistances = Distances(chain.Source, SourceReference, source.InheritedBoneCount);
            float[] targetDistances = Distances(chain.Target, TargetReference, target.InheritedBoneCount);
            Quaternion alignment = Quaternion.Normalize(chain.TargetAxes * Quaternion.Conjugate(chain.SourceAxes));
            foreach (short bone in chain.Source) required[bone] = true;
            int first = 0;
            for (int targetIndex = 0; targetIndex < chain.Target.Length; targetIndex++)
            {
                float distance = targetDistances[targetIndex] / targetDistances[^1] * sourceDistances[^1];
                while (first + 2 < sourceDistances.Length && sourceDistances[first + 1] < distance) first++;
                float amount = (distance - sourceDistances[first]) / (sourceDistances[first + 1] - sourceDistances[first]);
                int a = chain.Source[first], b = chain.Source[first + 1], bone = chain.Target[targetIndex];
                Quaternion reference = Quaternion.Slerp(Rotation(SourceReference[source.InheritedBoneCount + a]),
                    Rotation(SourceReference[source.InheritedBoneCount + b]), amount);
                Bones[bone] = new(a, b, amount, Quaternion.Conjugate(reference),
                    Rotation(TargetReference[target.InheritedBoneCount + bone]), alignment);
            }
            effectors[chainIndex] = chain.Target[^1];
            Ends[chainIndex] = new(chain.Source[^1], chain.Target[^1], alignment,
                targetDistances[^1] / sourceDistances[^1] * chain.GoalScale,
                SourceReference[source.InheritedBoneCount + chain.Source[^1]].Translation - SourceReference[source.InheritedBoneCount + root.Source].Translation,
                TargetReference[target.InheritedBoneCount + chain.Target[^1]].Translation - TargetReference[target.InheritedBoneCount + root.Target].Translation,
                chain.GoalWeight, chain.GoalOffset);
        }
        Bones[root.Target] = new(root.Source, root.Source, 0,
            Quaternion.Conjugate(Rotation(SourceReference[source.InheritedBoneCount + root.Source])),
            Rotation(TargetReference[target.InheritedBoneCount + root.Target]), RootAlignment);
        var selected = new List<short>();
        for (int bone = required.Length - 1; bone >= 0; bone--)
            if (required[bone] && source.Parents[bone] >= 0) required[source.Parents[bone]] = true;
        for (int bone = 0; bone < required.Length; bone++)
            if (required[bone]) selected.Add((short)bone);
        SourceBones = selected.ToArray();
        TargetBones = new short[target.BoneCount];
        for (int bone = 0; bone < TargetBones.Length; bone++) TargetBones[bone] = (short)bone;
        Correction = new(target, root.Target, effectors, settings, limits, tolerance, iterations);
    }

    /// <summary>Creates an ordinary deterministic target clip. Parent views read their completed current G.</summary>
    public AnimationClip CreateClip(AnimationClip source, ModelPose? sourceParent = null, ModelPose? targetParent = null) =>
        new(Target, source.Duration, ClipSampler.Create<RetargetClipData, RetargetSampler>(new(this, source, sourceParent, targetParent)),
            ImmutableArray.Create(source.Events.ToArray()), source.RootMotionBone >= 0 ? (short)Root.Target : (short)-1);

    public AnimationClip CreateClip(PosePath source, ModelPose? sourceParent = null, ModelPose? targetParent = null) =>
        CreateClip(source.Clip, sourceParent, targetParent);

    internal static Quaternion Rotation(in Matrix4x4 model)
    {
        Matrix4x4.Decompose(model, out _, out Quaternion rotation, out _);
        return rotation;
    }

    private static float[] Distances(ReadOnlySpan<short> bones, Matrix4x4[] reference, int inherited)
    {
        var distances = new float[bones.Length];
        for (int bone = 1; bone < bones.Length; bone++)
            distances[bone] = distances[bone - 1] + Vector3.Distance(reference[inherited + bones[bone - 1]].Translation,
                reference[inherited + bones[bone]].Translation);
        return distances;
    }

    private static Matrix4x4[] ReferenceModels(Skeleton skeleton)
    {
        var models = new Matrix4x4[skeleton.TotalBoneCount];
        if (skeleton.Parent is { } parentDefinition) ReferenceModels(parentDefinition).CopyTo(models, 0);
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
        {
            int parent = skeleton.Parents[bone] >= 0 ? skeleton.InheritedBoneCount + skeleton.Parents[bone] : skeleton.InheritedParents[bone];
            models[skeleton.InheritedBoneCount + bone] = skeleton.ReferencePose[bone].ToMatrix()
                * (parent >= 0 ? models[parent] : Matrix4x4.Identity);
        }
        return models;
    }
}

internal readonly record struct RetargetBone(int First, int Second, float Amount, Quaternion InverseReference,
    Quaternion TargetReference, Quaternion Alignment);
internal readonly record struct RetargetEnd(int Source, int Target, Quaternion Alignment, float Scale,
    Vector3 SourceReference, Vector3 TargetReference, float Weight, Vector3 Offset);
