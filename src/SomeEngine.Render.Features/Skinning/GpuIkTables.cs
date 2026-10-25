using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteSolverWork(BinaryWriter writer)
    {
        var children = new List<int>[_boneCount];
        var cpuBones = new bool[_boneCount];
        for (int bone = 0; bone < children.Length; bone++) children[bone] = [];
        int goals = 0;
        int xpbdPoints = 0;
        foreach (Section section in _sectionOrder)
        {
            section.XpbdStart = xpbdPoints;
            section.XpbdCount = 0;
            if (section.Data.Rig is { } installed)
                foreach (GpuPoseSource step in installed.Steps)
                    if (step is GpuXpbdPose xpbd) section.XpbdCount += xpbd.Chain.Bones.Length;
            xpbdPoints += section.XpbdCount;
            foreach (short bone in section.Data.CpuBones.Span) cpuBones[section.Range.Start + bone] = true;
            section.SolverEnd = -1; section.GoalStart = goals;
            goals += section.Data.Rig?.GoalCount ?? 0;
            for (int bone = 0; bone < section.Range.Count; bone++)
            {
                RenderBoneParent parent = section.Data.Parents[bone];
                if (parent.Section != Entity.Null)
                    children[_sections[parent.Section].Range.Start + parent.Bone].Add(section.Range.Start + bone);
            }
        }
        _xpbdPointCount = Math.Max(1, xpbdPoints);
        var descendants = new Dictionary<int, (uint Offset, uint Count)>();
        var levels = new SortedDictionary<(int Level, Kernel Kernel), List<SolverWork>>();
        foreach (Section section in _sectionOrder)
        {
            if (section.Data.Rig is not GpuPoseRig rig) continue;
            int level = End(section) - rig.Steps.Length - (rig.TranslationBone >= 0 ? 1 : 0);
            int xpbdState = section.XpbdStart;
            if (rig.TranslationBone >= 0)
            {
                (uint offset, uint count) = Descendants(section.Range.Start + rig.TranslationBone);
                uint definition = (uint)writer.BaseStream.Position;
                writer.Write(offset); writer.Write(count);
                Add(level++, Kernel.Translate, new((uint)section.Index, definition, 0));
            }
            foreach (GpuPoseSource source in rig.Steps)
            {
                if (source is GpuTwoBonePose limb)
                {
                    int root = section.Range.Start + limb.Chain.Root, middle = section.Range.Start + limb.Chain.Middle,
                        tip = section.Range.Start + limb.Chain.Tip;
                    if (cpuBones[root] && cpuBones[middle] && cpuBones[tip]) { level++; continue; }
                    uint definition = Limb(section, limb.Chain);
                    Add(level++, Kernel.TwoBone, new((uint)section.Index, definition, (uint)(section.GoalStart + limb.Goal)));
                }
                else if (source is GpuXpbdPose xpbd)
                {
                    var ranges = new (uint Offset, uint Count)[xpbd.Chain.Bones.Length];
                    for (int point = 0; point < ranges.Length; point++)
                        ranges[point] = Descendants(section.Range.Start + xpbd.Chain.Bones[point].Bone);
                    uint definition = WriteXpbdChain(writer, section, xpbd.Chain, ranges, xpbdState);
                    xpbdState += ranges.Length;
                    Add(level++, Kernel.Xpbd, new((uint)section.Index, definition, 0));
                }
                else if (source is GpuStridePose stride)
                {
                    GpuStrideRig definition = stride.Rig;
                    var limbs = new uint[definition.Legs.Length];
                    for (int foot = 0; foot < limbs.Length; foot++) limbs[foot] = Limb(section, definition.Legs[foot].Chain);
                    var pelvis = Descendants(section.Range.Start + definition.PelvisBone);
                    var spine = new (uint Offset, uint Count)[definition.Spine.Length];
                    for (int joint = 0; joint < spine.Length; joint++)
                        spine[joint] = Descendants(section.Range.Start + definition.Spine[joint].Bone);
                    Align(writer, 16);
                    uint legRows = (uint)writer.BaseStream.Position;
                    for (int foot = 0; foot < limbs.Length; foot++)
                    {
                        writer.Write(limbs[foot]); writer.Write((uint)(section.GoalStart + definition.Legs[foot].Goal));
                        writer.Write(0u); writer.Write(0u);
                    }
                    uint spineRows = (uint)writer.BaseStream.Position;
                    for (int joint = 0; joint < spine.Length; joint++)
                    {
                        writer.Write((uint)(section.Range.Start + definition.Spine[joint].Bone));
                        writer.Write(spine[joint].Offset); writer.Write(spine[joint].Count); writer.Write(definition.Spine[joint].Weight);
                    }
                    uint address = (uint)writer.BaseStream.Position;
                    writer.Write((uint)(section.Range.Start + definition.PelvisBone));
                    writer.Write(pelvis.Offset); writer.Write(pelvis.Count); writer.Write((uint)limbs.Length);
                    writer.Write(legRows); writer.Write(spineRows); writer.Write((uint)spine.Length); writer.Write(0u);
                    writer.Write(definition.Up.X); writer.Write(definition.Up.Y); writer.Write(definition.Up.Z); writer.Write(definition.MinimumScale);
                    writer.Write(definition.MaximumScale); writer.Write(definition.MaximumAngle); writer.Write(definition.MaximumPelvisAdjustment); writer.Write(0u);
                    writer.Write(definition.FacingAxis.X); writer.Write(definition.FacingAxis.Y); writer.Write(definition.FacingAxis.Z); writer.Write(0u);
                    Add(level++, Kernel.Stride, new((uint)section.Index, address, 0));
                }
                else
                {
                    var aim = (GpuAimPose)source;
                    var ranges = new (uint Offset, uint Count)[aim.Chain.Bones.Length];
                    for (int joint = 0; joint < ranges.Length; joint++)
                        ranges[joint] = Descendants(section.Range.Start + aim.Chain.Bones[joint].Bone);
                    Align(writer, 16);
                    uint definition = (uint)writer.BaseStream.Position;
                    writer.Write(definition + 16u); writer.Write((uint)ranges.Length); writer.Write(0u); writer.Write(0u);
                    for (int joint = 0; joint < ranges.Length; joint++)
                    {
                        GpuAimBone bone = aim.Chain.Bones[joint];
                        writer.Write((uint)(section.Range.Start + bone.Bone));
                        writer.Write(ranges[joint].Offset); writer.Write(ranges[joint].Count); writer.Write(0u);
                        writer.Write(bone.Axis.X); writer.Write(bone.Axis.Y); writer.Write(bone.Axis.Z); writer.Write(bone.Weight);
                        writer.Write(bone.MaximumAngle); writer.Write(0u); writer.Write(0u); writer.Write(0u);
                    }
                    Add(level++, Kernel.Aim, new((uint)section.Index, definition, (uint)(section.GoalStart + aim.Goal)));
                }
            }
        }
        _solverDispatches.Clear();
        foreach (var entry in levels)
        {
            Align(writer, 16);
            ulong start = (ulong)writer.BaseStream.Position;
            writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(entry.Value)));
            _solverDispatches.Add(new(entry.Key.Kernel, new(start, (ulong)entry.Value.Count * 16), entry.Value.Count));
        }

        uint Limb(Section section, GpuTwoBoneChain chain)
        {
            int root = section.Range.Start + chain.Root, middle = section.Range.Start + chain.Middle,
                tip = section.Range.Start + chain.Tip;
            var rootRange = Descendants(root); var middleRange = Descendants(middle); var tipRange = Descendants(tip);
            Align(writer, 16);
            uint address = (uint)writer.BaseStream.Position;
            writer.Write((uint)root); writer.Write((uint)middle); writer.Write((uint)tip); writer.Write(0u);
            writer.Write(MathF.Cos(chain.MinimumBend)); writer.Write(MathF.Cos(chain.MaximumBend)); writer.Write(0u); writer.Write(0u);
            writer.Write(rootRange.Offset); writer.Write(rootRange.Count); writer.Write(middleRange.Offset); writer.Write(middleRange.Count);
            writer.Write(tipRange.Offset); writer.Write(tipRange.Count); writer.Write(0u); writer.Write(0u);
            return address;
        }
        int End(Section section)
        {
            if (section.SolverEnd >= 0) return section.SolverEnd;
            int start = 0;
            foreach (RenderBoneParent parent in section.Data.Parents)
                if (parent.Section != Entity.Null && parent.Section != section.Entity)
                    start = Math.Max(start, End(_sections[parent.Section]));
            GpuPoseRig? rig = section.Data.Rig;
            return section.SolverEnd = start + (rig?.Steps.Length ?? 0) + (rig?.TranslationBone >= 0 ? 1 : 0);
        }
        (uint Offset, uint Count) Descendants(int root)
        {
            if (descendants.TryGetValue(root, out var range)) return range;
            var bones = new List<int>();
            var visit = new Stack<int>(); visit.Push(root);
            while (visit.TryPop(out int bone))
            {
                if (cpuBones[bone]) continue;
                bones.Add(bone);
                foreach (int child in children[bone]) visit.Push(child);
            }
            Align(writer, 4);
            range = ((uint)writer.BaseStream.Position, (uint)bones.Count);
            foreach (int bone in bones) writer.Write((uint)bone);
            descendants.Add(root, range);
            return range;
        }
        void Add(int level, Kernel kernel, SolverWork work)
        {
            if (!levels.TryGetValue((level, kernel), out var rows)) levels.Add((level, kernel), rows = []);
            rows.Add(work);
        }
    }

    private readonly record struct SolverWork(uint Section, uint Definition, uint Goal, uint Padding = 0);
    private readonly record struct SolverDispatch(Kernel Kernel, BufferRange Work, int Count);
}
