using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Runtime.Tests;

// This example supplies three TRS curves and two derived characters for the app behavior check.
internal static class GpuPoseExamples
{
    internal static (Skeleton Parent, Skeleton Child) Definitions()
    {
        BoneTransform identity = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var parent = new Skeleton([-1], [identity], [1]);
        return (parent, new Skeleton(parent, [-1, 0], [identity, identity], [2, 3], [0, -1], [0, 2]));
    }

    internal static AnimationClip Clip(Skeleton skeleton, int section) =>
        new(skeleton, 2, ClipSampler.Create<ClipSection, TrsSampler>(new(section)), [], -1);

    private sealed record ClipSection(int Section);
    private readonly struct TrsSampler : IClipSampler<ClipSection>
    {
        public static void SampleLocal(ClipSection data, float time, Span<BoneTransform> destination)
        {
            for (int bone = 0; bone < destination.Length; bone++)
            {
                float factor = 1 + bone + data.Section;
                destination[bone] = new()
                {
                    Translation = new(.2f * factor * time, .1f * factor * time, 0),
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f * factor * time),
                    Scale = new(1 + .1f * time, 1 + .04f * factor * time, 1),
                };
            }
        }
    }

    internal static void Near(Matrix4x4 expected, Matrix4x4 actual)
    {
        ReadOnlySpan<float> a = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in expected, 1));
        ReadOnlySpan<float> b = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in actual, 1));
        for (int index = 0; index < 16; index++) Assert.InRange(MathF.Abs(a[index] - b[index]), 0, .001f);
    }

    internal sealed class RepeatLastInput(Entity[] targets) : ISystem<ImmediateSystemContext>
    {
        private readonly GpuPoseTarget[] _inputs = new GpuPoseTarget[targets.Length];
        public bool Repeat;
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            for (int index = 0; index < targets.Length; index++)
                if (Repeat) context.World.Replace(targets[index], _inputs[index]);
                else _inputs[index] = context.World.Read<GpuPoseTarget>(targets[index]);
        }
    }
}
