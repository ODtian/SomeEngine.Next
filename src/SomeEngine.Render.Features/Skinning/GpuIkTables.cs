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
        for (int bone = 0; bone < children.Length; bone++) children[bone] = [];
        int goals = 0;
        foreach (Section section in _sectionOrder)
        {
            section.SolverEnd = -1; section.GoalStart = goals;
            goals += section.Data.Rig?.GoalCount ?? 0;
            for (int bone = 0; bone < section.Range.Count; bone++)
            {
                RenderBoneParent parent = section.Data.Parents[bone];
                if (parent.Section != Entity.Null)
                    children[_sections[parent.Section].Range.Start + parent.Bone].Add(section.Range.Start + bone);
            }
        }
        var descendants = new Dictionary<int, (uint Offset, uint Count)>();
        var levels = new SortedDictionary<(int Level, Kernel Kernel), List<SolverWork>>();
        foreach (Section section in _sectionOrder)
        {
            if (section.Data.Rig is not GpuPoseRig rig) continue;
            int level = End(section) - rig.Steps.Length - (rig.TranslationBone >= 0 ? 1 : 0);
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
                    var rootRange = Descendants(root); var middleRange = Descendants(middle); var tipRange = Descendants(tip);
                    Align(writer, 16);
                    uint definition = (uint)writer.BaseStream.Position;
                    writer.Write((uint)root); writer.Write((uint)middle); writer.Write((uint)tip); writer.Write(0u);
                    writer.Write(MathF.Cos(limb.Chain.MinimumBend)); writer.Write(MathF.Cos(limb.Chain.MaximumBend));
                    writer.Write(0u); writer.Write(0u);
                    writer.Write(rootRange.Offset); writer.Write(rootRange.Count);
                    writer.Write(middleRange.Offset); writer.Write(middleRange.Count);
                    writer.Write(tipRange.Offset); writer.Write(tipRange.Count); writer.Write(0u); writer.Write(0u);
                    Add(level++, Kernel.TwoBone, new((uint)section.Index, definition, (uint)(section.GoalStart + limb.Goal)));
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
