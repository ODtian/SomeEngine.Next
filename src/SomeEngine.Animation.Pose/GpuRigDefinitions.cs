namespace SomeEngine.Animation.Pose;

/// <summary>Shared model-space rig definitions and their derived descendant ranges.</summary>
public sealed class GpuRigDefinitions(BinaryWriter writer, int boneStart, int goalStart,
    Func<int, (uint Offset, uint Count)> descendants)
{
    public uint WriteTranslation(int bone)
    {
        var range = descendants(boneStart + bone);
        uint address = (uint)writer.BaseStream.Position;
        writer.Write(range.Offset); writer.Write(range.Count);
        return address;
    }

    public uint WriteTwoBone(GpuTwoBoneChain chain)
    {
        int root = boneStart + chain.Root, middle = boneStart + chain.Middle, tip = boneStart + chain.Tip;
        var rootRange = descendants(root); var middleRange = descendants(middle); var tipRange = descendants(tip);
        Align(16);
        uint address = (uint)writer.BaseStream.Position;
        writer.Write((uint)root); writer.Write((uint)middle); writer.Write((uint)tip); writer.Write(0u);
        writer.Write(MathF.Cos(chain.MinimumBend)); writer.Write(MathF.Cos(chain.MaximumBend)); writer.Write(0u); writer.Write(0u);
        writer.Write(rootRange.Offset); writer.Write(rootRange.Count); writer.Write(middleRange.Offset); writer.Write(middleRange.Count);
        writer.Write(tipRange.Offset); writer.Write(tipRange.Count); writer.Write(0u); writer.Write(0u);
        return address;
    }

    public uint WriteAim(GpuAimChain chain)
    {
        var ranges = new (uint Offset, uint Count)[chain.Bones.Length];
        for (int joint = 0; joint < ranges.Length; joint++) ranges[joint] = descendants(boneStart + chain.Bones[joint].Bone);
        Align(16);
        uint address = (uint)writer.BaseStream.Position;
        writer.Write(address + 16u); writer.Write((uint)ranges.Length); writer.Write(0u); writer.Write(0u);
        for (int joint = 0; joint < ranges.Length; joint++)
        {
            GpuAimBone bone = chain.Bones[joint];
            writer.Write((uint)(boneStart + bone.Bone));
            writer.Write(ranges[joint].Offset); writer.Write(ranges[joint].Count); writer.Write(0u);
            writer.Write(bone.Axis.X); writer.Write(bone.Axis.Y); writer.Write(bone.Axis.Z); writer.Write(bone.Weight);
            writer.Write(bone.MaximumAngle); writer.Write(0u); writer.Write(0u); writer.Write(0u);
        }
        return address;
    }

    public uint WriteStride(GpuStrideRig rig)
    {
        var limbs = new uint[rig.Legs.Length];
        for (int foot = 0; foot < limbs.Length; foot++) limbs[foot] = WriteTwoBone(rig.Legs[foot].Chain);
        var pelvis = descendants(boneStart + rig.PelvisBone);
        var spine = new (uint Offset, uint Count)[rig.Spine.Length];
        for (int joint = 0; joint < spine.Length; joint++) spine[joint] = descendants(boneStart + rig.Spine[joint].Bone);
        Align(16);
        uint legRows = (uint)writer.BaseStream.Position;
        for (int foot = 0; foot < limbs.Length; foot++)
        {
            writer.Write(limbs[foot]); writer.Write((uint)(goalStart + rig.Legs[foot].Goal));
            writer.Write(0u); writer.Write(0u);
        }
        uint spineRows = (uint)writer.BaseStream.Position;
        for (int joint = 0; joint < spine.Length; joint++)
        {
            writer.Write((uint)(boneStart + rig.Spine[joint].Bone));
            writer.Write(spine[joint].Offset); writer.Write(spine[joint].Count); writer.Write(rig.Spine[joint].Weight);
        }
        uint address = (uint)writer.BaseStream.Position;
        writer.Write((uint)(boneStart + rig.PelvisBone));
        writer.Write(pelvis.Offset); writer.Write(pelvis.Count); writer.Write((uint)limbs.Length);
        writer.Write(legRows); writer.Write(spineRows); writer.Write((uint)spine.Length); writer.Write(0u);
        writer.Write(rig.Up.X); writer.Write(rig.Up.Y); writer.Write(rig.Up.Z); writer.Write(rig.MinimumScale);
        writer.Write(rig.MaximumScale); writer.Write(rig.MaximumAngle); writer.Write(rig.MaximumPelvisAdjustment); writer.Write(0u);
        writer.Write(rig.FacingAxis.X); writer.Write(rig.FacingAxis.Y); writer.Write(rig.FacingAxis.Z); writer.Write(0u);
        return address;
    }

    private void Align(int alignment)
    {
        while (writer.BaseStream.Position % alignment != 0) writer.Write((byte)0);
    }
}
