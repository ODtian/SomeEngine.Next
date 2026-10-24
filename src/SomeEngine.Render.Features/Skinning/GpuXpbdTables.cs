using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private readonly LinkedComputePipeline _xpbdPipeline;
    private readonly GraphBufferInput _xpbdFramesInput, _xpbdPointsInput, _xpbdWorkInput;
    private readonly List<XpbdFrame> _xpbdFrames = [];
    private GraphBufferId _xpbdPoints, _readyXpbdPoints;
    private int _xpbdPointCount = 1;

    private static uint WriteXpbdChain(BinaryWriter writer, Section section, GpuXpbdChain chain,
        ReadOnlySpan<(uint Offset, uint Count)> descendants, int state)
    {
        Align(writer, 16);
        uint points = (uint)writer.BaseStream.Position;
        for (int index = 0; index < chain.Bones.Length; index++)
        {
            GpuXpbdBone bone = chain.Bones[index];
            Vector3 rest = index == 0 ? Vector3.Zero : chain.Skeleton.ReferencePose[bone.Bone].Translation;
            writer.Write((uint)(section.Range.Start + bone.Bone));
            writer.Write(descendants[index].Offset); writer.Write(descendants[index].Count); writer.Write(0u);
            writer.Write(rest.X); writer.Write(rest.Y); writer.Write(rest.Z); writer.Write(bone.InverseMass);
        }
        uint definition = (uint)writer.BaseStream.Position;
        writer.Write(points); writer.Write((uint)chain.Bones.Length); writer.Write((uint)state); writer.Write((uint)chain.Iterations);
        writer.Write(chain.LengthCompliance); writer.Write(chain.BendCompliance); writer.Write(MathF.Cos(chain.MaximumBend)); writer.Write(chain.Damping);
        writer.Write(chain.Acceleration.X); writer.Write(chain.Acceleration.Y); writer.Write(chain.Acceleration.Z); writer.Write(chain.Step);
        return definition;
    }

    private static XpbdFrame ReadXpbdFrame(Section section)
    {
        if (section.XpbdCount == 0) return default;
        RenderBoneSectionData data = section.Data;
        Matrix4x4.Invert(data.ModelToWorld, out Matrix4x4 inverse);
        float delta = section.ResetXpbd ? 0 : (float)(section.FrameTime - section.XpbdTime);
        return new(data.ModelToWorld, inverse, new(delta, section.ResetXpbd ? 1 : 0, 0, 0));
    }

    private readonly record struct XpbdFrame(Matrix4x4 ModelToWorld, Matrix4x4 WorldToModel, Vector4 Step);
    private readonly record struct XpbdBinding(Entity Target, SkeletonInstance Instance, GpuPoseRig? Rig,
        uint Starts, uint AnimationStarts, ulong SkeletonBinding);
}
