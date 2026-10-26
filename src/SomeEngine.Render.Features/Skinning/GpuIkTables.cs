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
            var definitions = new GpuRigDefinitions(writer, section.Range.Start, section.GoalStart, Descendants);
            if (rig.TranslationBone >= 0)
            {
                Add(level++, Kernel.Translate, new((uint)section.Index, definitions.WriteTranslation(rig.TranslationBone), 0));
            }
            foreach (GpuPoseSource source in rig.Steps)
            {
                if (source is GpuTwoBonePose limb)
                {
                    int root = section.Range.Start + limb.Chain.Root, middle = section.Range.Start + limb.Chain.Middle,
                        tip = section.Range.Start + limb.Chain.Tip;
                    if (cpuBones[root] && cpuBones[middle] && cpuBones[tip]) { level++; continue; }
                    uint definition = definitions.WriteTwoBone(limb.Chain);
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
                    Add(level++, Kernel.Stride, new((uint)section.Index, definitions.WriteStride(stride.Rig), 0));
                }
                else
                {
                    var aim = (GpuAimPose)source;
                    Add(level++, Kernel.Aim, new((uint)section.Index, definitions.WriteAim(aim.Chain), (uint)(section.GoalStart + aim.Goal)));
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
