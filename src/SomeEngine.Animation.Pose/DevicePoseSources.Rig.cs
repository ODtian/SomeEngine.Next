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
        var levels = new SortedDictionary<(int Level, int Kernel), List<BoneWork>>();
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
            _rigOperations.Add(new(group.Key.Kernel, OrderBindings(_layouts[group.Key.Kernel], _bindings), group.Value.Count));
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
                writer.Write((uint)bone); count++;
                foreach (int child in children[bone]) visit.Push(child);
            }
            range = (first, count); ranges.Add(root, range);
            return range;
        }
        void Add(int level, SourceRow row, int kernel, uint definition, uint goal)
        {
            if (!levels.TryGetValue((level, kernel), out var work)) levels.Add((level, kernel), work = []);
            work.Add(new((uint)row.Index, definition, goal, 0));
        }
    }
}
