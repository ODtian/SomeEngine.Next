using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;

namespace SomeEngine.Animation.Pose;

public sealed partial class DevicePoseSources
{
    private readonly Dictionary<SkeletonInstance, PoseRange> _cpuSections = [];
    private readonly Dictionary<SkeletonInstance, SourceRow> _gpuSections = [];
    private readonly List<PoseRange> _cpuCopies = [];
    private readonly HashSet<int> _cpuWritten = [];
    private UploadBuffer _cpuModels = null!;
    private bool _statesInitialized;

    private void AddCpuSections(ref int bones)
    {
        _cpuSections.Clear(); _gpuSections.Clear(); _cpuWritten.Clear();
        foreach (SourceRow source in _order) _gpuSections.Add(source.Input.Skeleton, source);
        foreach (SourceRow source in _order)
            foreach (short bone in source.Input.Skeleton.CpuBones.Span) _cpuWritten.Add(source.Range.Start + bone);
        foreach (SourceRow source in _order)
            foreach (SkeletonInstance section in source.Input.Skeleton.Sections)
            {
                if (_gpuSections.ContainsKey(section) || _cpuSections.ContainsKey(section)) continue;
                _cpuSections.Add(section, new(bones, section.Skeleton.BoneCount));
                bones += section.Skeleton.BoneCount;
            }
    }

    private void CaptureCpuModels()
    {
        _cpuCopies.Clear();
        Span<Matrix4x4> matrices = MemoryMarshal.Cast<byte, Matrix4x4>(_cpuModels.Mapping.Bytes);
        foreach ((SkeletonInstance source, PoseRange range) in _cpuSections)
        {
            source.ModelTransforms.CopyTo(matrices.Slice(range.Start, range.Count));
            _cpuCopies.Add(range);
        }
        foreach (SourceRow source in _order)
        {
            foreach (short bone in source.Input.Skeleton.CpuBones.Span)
            {
                int destination = source.Range.Start + bone;
                matrices[destination] = source.Input.Skeleton.ModelTransforms[bone];
                _cpuCopies.Add(new(destination, 1));
            }
        }
        foreach (PoseRange copy in _cpuCopies)
            _cpuModels.Mapping.Flush(new((ulong)copy.Start * 64, (ulong)copy.Count * 64));
    }

    private void CopyCpuModels()
    {
        if (!_statesInitialized)
        {
            foreach (var state in _states)
                _backend.Barrier(_commands, new BufferBarrier(state, state.InitialSync, PipelineSync.ComputeShading,
                    state.InitialAccess, ResourceAccess.UnorderedAccess));
            _statesInitialized = true;
        }
        if (_cpuCopies.Count == 0) return;
        _backend.Barrier(_commands, new BufferBarrier(Models, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        foreach (PoseRange copy in _cpuCopies)
        {
            ulong start = (ulong)copy.Start * 64;
            _backend.CopyBuffer(_commands, new(_cpuModels.Buffer, start, Models, start, (ulong)copy.Count * 64));
        }
        _backend.Barrier(_commands, new BufferBarrier(Models, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
    }

    private (SourceRow? Source, int Bone, int Index) Parent(SourceRow source, int bone)
    {
        Skeleton skeleton = source.Input.Skeleton.Skeleton;
        int parent = skeleton.Parents[bone], inherited = skeleton.InheritedParents[bone];
        if (parent >= 0) return (source, parent, source.Range.Start + parent);
        if (inherited < 0) return (null, -1, -1);
        BoneSource link = source.Input.Skeleton.BoneSources[inherited];
        return _gpuSections.TryGetValue(link.Instance, out SourceRow? section)
            ? (section, link.Bone, section.Range.Start + link.Bone)
            : (null, link.Bone, _cpuSections[link.Instance].Start + link.Bone);
    }

    private static bool CpuWritten(SourceRow source, int bone) =>
        source.Input.Skeleton.CpuBones.Span.BinarySearch((short)bone) >= 0;
}
