using System.Buffers;
using System.Numerics;

namespace SomeEngine.Animation.Pose;

public sealed partial class PosePath
{
    /// <summary>Repeats a finite path on a cumulative clock, carrying its declared root displacement and rotation.</summary>
    public static PosePath Loop(PosePath part)
    {
        var data = new LoopData(part);
        return new(new AnimationClip(part.Skeleton, float.PositiveInfinity,
            ClipSampler.Create<LoopData, LoopSampler>(data), rootMotionBone: data.RootBone), data);
    }

    private sealed class LoopData
    {
        internal readonly PosePath Part;
        internal readonly Matrix4x4 Cycle;
        internal readonly short RootBone;
        internal readonly short[] Roots;

        internal LoopData(PosePath part)
        {
            Part = part;
            RootBone = part.FindFootMotion()?.RootBone ?? part.Clip.RootMotionBone;
            Cycle = Matrix4x4.Identity;
            if (RootBone >= 0)
            {
                var batch = new ClipSampleBatch([new(part.Clip, [RootBone]), new(part.Clip, [RootBone])]);
                Span<Matrix4x4> roots = stackalloc Matrix4x4[2];
                batch.SampleModel([0, part.Duration], roots);
                Matrix4x4.Decompose(roots[0], out _, out Quaternion startRotation, out Vector3 startPosition);
                Matrix4x4.Decompose(roots[1], out _, out Quaternion endRotation, out Vector3 endPosition);
                ReadOnlySpan<RootMotionKey> keys = [
                    new(0, startPosition, startRotation), new(part.Duration, endPosition, endRotation),
                ];
                Cycle = new RootMotionPath(keys).Sample(part.Duration);
            }
            var indices = new List<short>();
            for (int bone = 0; bone < part.Skeleton.BoneCount; bone++)
                if (part.Skeleton.Parents[bone] < 0 && part.Skeleton.InheritedParents[bone] < 0) indices.Add((short)bone);
            Roots = indices.ToArray();
        }

        internal Matrix4x4 Movement(float time) => ClipRootMotion.Power(Cycle, (int)(time / Part.Duration));
        internal float Phase(float time) => time % Part.Duration;
    }

    private readonly struct LoopSampler : IClipSampler<LoopData>
    {
        public static void SampleLocal(LoopData data, float time, Span<BoneTransform> destination)
        {
            data.Part.Clip.SampleLocal(data.Phase(time), destination);
            TransformRoots(data, time, destination);
        }

        public static void SampleLocalBatch<TSampler>(LoopData data, ref ClipSamples samples)
            where TSampler : struct, IClipSampler<LoopData>
        {
            float[] phases = ArrayPool<float>.Shared.Rent(samples.Count);
            try
            {
                for (int sample = 0; sample < samples.Count; sample++) phases[sample] = data.Phase(samples.Times[sample]);
                var source = new ClipSamples(phases.AsSpan(0, samples.Count), samples.Bones, samples.LocalPoses, samples.BoneCount);
                data.Part.Clip.SampleLocal(ref source);
                for (int sample = 0; sample < samples.Count; sample++)
                    TransformRoots(data, samples.Times[sample], samples.LocalPose(sample));
            }
            finally { ArrayPool<float>.Shared.Return(phases); }
        }

        private static void TransformRoots(LoopData data, float time, Span<BoneTransform> pose)
        {
            Matrix4x4 movement = data.Movement(time);
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(movement);
            foreach (short bone in data.Roots)
            {
                pose[bone].Translation = Vector3.Transform(pose[bone].Translation, movement);
                pose[bone].Rotation = Quaternion.Concatenate(pose[bone].Rotation, rotation);
            }
        }
    }
}
