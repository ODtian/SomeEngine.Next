using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePoseSources
{
    private void WriteRigOperations(BinaryWriter writer, List<int>[] children, ref int workOffset)
    {
        var sections = _order.ToDictionary(row => row.Input.Skeleton);
        var ranges = new Dictionary<int, (uint Offset, uint Count)>();
        var ends = new Dictionary<SourceRow, int>();
        var levels = new SortedDictionary<(int Level, int Kernel, bool AfterPhysics), List<BoneWork>>();
        _rigOperations.Clear();
        int offset = workOffset;
        foreach (SourceRow row in _order.OrderBy(End))
        {
            if (row.Input.Rig is not { } rig) continue;
            var definitions = new GpuRigDefinitions(writer, row.Range.Start, row.GoalStart, Descendants);
            int level = End(row) - rig.Steps.Length - (rig.TranslationBone >= 0 ? 1 : 0);
            if (rig.TranslationBone >= 0) Add(level++, row, _translateKernel, definitions.WriteTranslation(rig.TranslationBone), 0);
            for (int stepIndex = 0; stepIndex < rig.Steps.Length; stepIndex++)
            {
                GpuPoseSource step = rig.Steps[stepIndex];
                switch (step)
                {
                    case GpuTwoBonePose limb:
                        if (CpuWritten(row, limb.Chain.Root) && CpuWritten(row, limb.Chain.Middle) && CpuWritten(row, limb.Chain.Tip)) { level++; break; }
                        Add(level++, row, _twoBoneKernel, definitions.WriteTwoBone(limb.Chain), (uint)(row.GoalStart + limb.Goal));
                        break;
                    case GpuAimPose aim:
                        Add(level++, row, _aimKernel, definitions.WriteAim(aim.Chain), (uint)(row.GoalStart + aim.Goal));
                        break;
                    case GpuStridePose stride:
                        Add(level++, row, _strideKernel, definitions.WriteStride(stride.Rig), 0);
                        break;
                    case GpuXpbdPose:
                        Add(level++, row, _xpbdKernel, _physics!.WriteDefinition(writer, row.Entity, stepIndex, row.Range.Start, Descendants), 0);
                        break;
                }
            }
        }
        foreach (var group in levels)
        {
            ReadOnlySpan<byte> work = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(group.Value));
            work.CopyTo(_work.Mapping.Bytes[offset..]);
            BufferSrv view = _backend.CreateBufferSrv(_device, new(_work.Buffer, new((ulong)offset, (ulong)work.Length), StructureStride: 16));
            _workViews.Add(view); _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(view)];
            _rigOperations.Add(new(group.Key.Kernel, view, [], group.Value.Count, group.Key.AfterPhysics));
            offset += work.Length;
        }
        workOffset = offset;

        int End(SourceRow row)
        {
            if (ends.TryGetValue(row, out int end)) return end;
            int start = row.Input.Skeleton.ParentInstance is { } parent && sections.TryGetValue(parent, out SourceRow? source) ? End(source) : 0;
            GpuPoseRig? rig = row.Input.Rig;
            end = start + (rig?.Steps.Length ?? 0) + (rig?.TranslationBone >= 0 ? 1 : 0);
            ends.Add(row, end);
            return end;
        }
        (uint Offset, uint Count) Descendants(int root)
        {
            if (ranges.TryGetValue(root, out var range)) return range;
            while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
            uint first = (uint)writer.BaseStream.Position, count = 0;
            var visit = new Stack<int>(); visit.Push(root);
            while (visit.TryPop(out int bone))
            {
                if (_cpuWritten.Contains(bone)) continue;
                writer.Write((uint)bone); count++;
                foreach (int child in children[bone]) visit.Push(child);
            }
            range = (first, count); ranges.Add(root, range);
            return range;
        }
        void Add(int level, SourceRow row, int kernel, uint definition, uint goal)
        {
            bool afterPhysics = false;
            for (SkeletonInstance? section = row.Input.Skeleton; section is not null; section = section.ParentInstance)
                if (sections.TryGetValue(section, out SourceRow? source) && source.Input.Rig is { } rig)
                {
                    int start = End(source) - rig.Steps.Length - (rig.TranslationBone >= 0 ? 1 : 0);
                    for (int step = 0; step < rig.Steps.Length; step++)
                        if (rig.Steps[step] is GpuXpbdPose && start + (rig.TranslationBone >= 0 ? 1 : 0) + step < level)
                            afterPhysics = true;
                }
            var key = (level, kernel, afterPhysics);
            if (!levels.TryGetValue(key, out var work)) levels.Add(key, work = []);
            work.Add(new((uint)row.Index, definition, goal, 0));
        }
    }
}
