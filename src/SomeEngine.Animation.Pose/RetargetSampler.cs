using System.Buffers;
using System.Numerics;

namespace SomeEngine.Animation.Pose;

internal sealed record RetargetClipData(RetargetMapping Mapping, AnimationClip Source,
    ModelPose? SourceParent, ModelPose? TargetParent);

internal readonly struct RetargetSampler : IClipSampler<RetargetClipData>
{
    public static void SampleLocal(RetargetClipData data, float time, Span<BoneTransform> destination)
    {
        ReadOnlySpan<float> times = [time];
        var samples = new ClipSamples(times, data.Mapping.TargetBones, destination, data.Mapping.Target.BoneCount);
        Sample(data, ref samples);
    }

    public static void SampleLocalBatch<TSampler>(RetargetClipData data, ref ClipSamples samples)
        where TSampler : struct, IClipSampler<RetargetClipData> => Sample(data, ref samples);

    private static void Sample(RetargetClipData data, ref ClipSamples samples)
    {
        RetargetMapping map = data.Mapping;
        int sourceCount = map.Source.BoneCount, targetCount = map.Target.BoneCount, bodyCount = map.Correction.Bodies.Length;
        BoneTransform[] localArray = ArrayPool<BoneTransform>.Shared.Rent(sourceCount * samples.Count);
        Matrix4x4[] modelArray = ArrayPool<Matrix4x4>.Shared.Rent(2 * (sourceCount + targetCount));
        Vector3[] vectorArray = ArrayPool<Vector3>.Shared.Rent(3 * bodyCount + map.Ends.Length);
        Quaternion[] rotationArray = ArrayPool<Quaternion>.Shared.Rent(bodyCount);
        PbikTarget[] goalArray = ArrayPool<PbikTarget>.Shared.Rent(map.Ends.Length);
        try
        {
            var source = new ClipSamples(samples.Times, map.SourceBones, localArray, sourceCount);
            data.Source.SampleLocal(ref source);
            Span<Matrix4x4> sourceModel = modelArray.AsSpan(0, sourceCount), targetModel = modelArray.AsSpan(sourceCount, targetCount);
            Span<Matrix4x4> sourceInherited = modelArray.AsSpan(sourceCount + targetCount, sourceCount);
            Span<Matrix4x4> targetInherited = modelArray.AsSpan(2 * sourceCount + targetCount, targetCount);
            ReadParents(map.Source, data.SourceParent, map.SourceReference, sourceInherited);
            ReadParents(map.Target, data.TargetParent, map.TargetReference, targetInherited);
            Span<PbikTarget> goals = goalArray.AsSpan(0, map.Ends.Length);
            for (int sample = 0; sample < samples.Count; sample++)
            {
                Span<BoneTransform> sourceLocal = source.LocalPose(sample), targetLocal = samples.LocalPose(sample);
                foreach (short bone in map.SourceBones)
                    sourceModel[bone] = sourceLocal[bone].ToMatrix() * ParentModel(map.Source, bone, sourceModel, sourceInherited);
                Vector3 sourceRoot = sourceModel[map.Root.Source].Translation;
                Vector3 rootChange = Vector3.Transform(sourceRoot
                    - map.SourceReference[map.Source.InheritedBoneCount + map.Root.Source].Translation,
                    Quaternion.Conjugate(map.Root.SourceAxes)) * map.Root.TranslationScale;
                Vector3 rootPosition = map.TargetReference[map.Target.InheritedBoneCount + map.Root.Target].Translation
                    + Vector3.Transform(rootChange, map.Root.TargetAxes);
                map.Target.ReferencePose.CopyTo(targetLocal);
                for (int bone = 0; bone < targetCount; bone++)
                {
                    Matrix4x4 parent = ParentModel(map.Target, bone, targetModel, targetInherited);
                    RetargetBone binding = map.Bones[bone];
                    if (binding.First >= 0)
                    {
                        Quaternion rotation = Quaternion.Slerp(RetargetMapping.Rotation(sourceModel[binding.First]),
                            RetargetMapping.Rotation(sourceModel[binding.Second]), binding.Amount);
                        Quaternion delta = binding.Alignment * rotation * binding.InverseReference * Quaternion.Conjugate(binding.Alignment);
                        targetLocal[bone].Rotation = Quaternion.Normalize(Quaternion.Conjugate(RetargetMapping.Rotation(parent))
                            * delta * binding.TargetReference);
                    }
                    if (bone == map.Root.Target)
                    {
                        Matrix4x4.Invert(parent, out Matrix4x4 inverseParent);
                        targetLocal[bone].Translation = Vector3.Transform(rootPosition, inverseParent);
                    }
                    targetModel[bone] = targetLocal[bone].ToMatrix() * parent;
                }
                for (int end = 0; end < goals.Length; end++)
                {
                    RetargetEnd definition = map.Ends[end];
                    Vector3 change = sourceModel[definition.Source].Translation - sourceRoot - definition.SourceReference;
                    goals[end] = new(rootPosition + definition.TargetReference
                        + Vector3.Transform(change, definition.Alignment) * definition.Scale + definition.Offset, definition.Weight);
                }
                var solver = new PbikSolver(map.Correction, targetLocal, goals, targetModel, targetInherited,
                    vectorArray.AsSpan(0, bodyCount), vectorArray.AsSpan(bodyCount, bodyCount),
                    vectorArray.AsSpan(3 * bodyCount, goals.Length), vectorArray.AsSpan(2 * bodyCount, bodyCount),
                    rotationArray.AsSpan(0, bodyCount));
                solver.Solve();
            }
        }
        finally
        {
            ArrayPool<BoneTransform>.Shared.Return(localArray);
            ArrayPool<Matrix4x4>.Shared.Return(modelArray);
            ArrayPool<Vector3>.Shared.Return(vectorArray);
            ArrayPool<Quaternion>.Shared.Return(rotationArray);
            ArrayPool<PbikTarget>.Shared.Return(goalArray);
        }
    }

    private static void ReadParents(Skeleton skeleton, ModelPose? parent, Matrix4x4[] reference, Span<Matrix4x4> inherited)
    {
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
            if (skeleton.InheritedParents[bone] >= 0)
                inherited[bone] = parent is { } pose ? pose.GetBone(skeleton.InheritedParents[bone]).Transform
                    : reference[skeleton.InheritedParents[bone]];
    }

    private static Matrix4x4 ParentModel(Skeleton skeleton, int bone, Span<Matrix4x4> model, Span<Matrix4x4> inherited) =>
        skeleton.Parents[bone] >= 0 ? model[skeleton.Parents[bone]] : skeleton.InheritedParents[bone] >= 0 ? inherited[bone] : Matrix4x4.Identity;
}
