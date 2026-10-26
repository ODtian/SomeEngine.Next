using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

public readonly struct GpuPoseTargetResources : IJobComponentResources<GpuPoseTarget>
{
    public static long GetVersion(in GpuPoseTarget component) => unchecked((long)component.Skeleton.BindingVersion);
    public static void Collect(in GpuPoseTarget component, ref JobResourceAccessBuilder resources)
    {
        for (SkeletonInstance? section = component.Skeleton; section is not null; section = section.ParentInstance) resources.Read(section);
        if (component.Controls is { } controls) resources.Read(controls.AsMemory());
        if (!component.Readbacks.IsEmpty) resources.Read(component.Readbacks);
    }
}

public sealed partial class DevicePoseSources
{
    internal JobHandle ScheduleEvaluate(JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobHandle members = scheduler.Schedule(new CaptureEntities(this),
            [.. _queryAccesses, JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
        var writes = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        foreach (UploadBuffer upload in _uploads)
            writes.Add(JobResourceAccess.Write(scheduler.GetResourceToken(upload.Buffer), 0, (long)upload.Buffer.Info.Size));
        JobHandle captured = _resources.ScheduleEntities(_world, _targets, new SourceEntities(this),
            new CaptureInputs(this), writes.ToArray(), members);
        JobHandle complete = DevicePhysicsJobs.Schedule(scheduler, new Submission(this, false), GetDeviceAccesses(scheduler, false), captured);
        return scheduler.Schedule(new AcceptInputs(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], complete);
    }
    private readonly struct SourceEntities(DevicePoseSources sources) : IQueryReadEntities
    {
        public int Count => sources._entityCount;
        public Entity GetEntity(int index) => sources._entities[index];
    }
    private readonly struct CaptureEntities(DevicePoseSources sources) : IJob
    {
        public void Execute()
        {
            sources._entityCount = 0;
            DevicePoseSources current = sources;
            sources._world.ExecuteReadQuery(sources._targets, ref current, static (scoped QueryCursor rows, ref DevicePoseSources sources) =>
            {
                foreach (QueryRow row in rows.Rows) sources._entities[sources._entityCount++] = row.Entity;
            });
        }
    }
    private readonly struct CaptureInputs(DevicePoseSources sources) : IJob
    {
        public void Execute() => sources.Capture();
    }
    private readonly struct AcceptInputs(DevicePoseSources sources) : IJob
    {
        public void Execute()
        {
            foreach (SourceRow row in sources._order)
            {
                row.Accepted = row.Input; row.AcceptedBinding = row.Binding;
                row.HasInput = true; row.Reset = false;
            }
        }
    }
    private void Capture()
    {
        bool rebuild = _order.Count != _entityCount;
        _order.Clear();
        DevicePoseSources sources = this;
        _world.ExecuteReadQuery(_targets, ref sources, (scoped QueryCursor rows, ref DevicePoseSources current) =>
        {
            for (int index = 0; index < current._entityCount; index++)
            {
                Entity entity = current._entities[index];
                if (!rows.TryGetRow(entity, out QueryRow row)) continue;
                GpuPoseTarget input = row.Read<GpuPoseTarget>();
                if (!current._sources.TryGetValue(entity, out SourceRow? source))
                { current._sources.Add(entity, source = new(entity)); rebuild = true; }
                if (source.Index != current._order.Count || !ReferenceEquals(input.Skeleton, source.Input.Skeleton)
                    || !ReferenceEquals(input.Clip, source.Input.Clip) || !ReferenceEquals(input.StateMachine, source.Input.StateMachine)
                    || !ReferenceEquals(input.Blend, source.Input.Blend) || source.AcceptedBinding != input.Skeleton.BindingVersion) rebuild = true;
                source.Input = input; source.Binding = input.Skeleton.BindingVersion;
                source.Index = current._order.Count; current._order.Add(source);
            }
        });
        if (rebuild) Rebuild();
        int inputs = 0;
        foreach (SourceRow row in _order)
        {
            GpuPoseTarget input = row.Input;
            bool reset = row.Reset || !row.HasInput || row.Accepted.Starts != input.Starts
                || row.Accepted.AnimationStarts != input.AnimationStarts;
            bool advance = reset || row.Accepted.InputNumber != input.InputNumber;
            uint flags = 1u | 4u | (reset ? 2u | 32u | 512u : 0u) | (input.Playing ? 16u : 0u)
                | (input.StateMachine is not null ? 8u : 0u) | (input.Blend is not null ? 64u : 0u)
                | (advance ? 128u | 256u : 0u);
            var control = new Control(input.Blend is null ? row.Clip : (uint)row.BlendLocalStart, input.SampleTime,
                flags, input.Starts, input.InputNumber, input.CalculationTime,
                input.Blend is null ? row.Machine : row.Blend, (uint)inputs, input.PlaybackTime,
                (uint)row.BlendStateStart, row.Reference, 1, 0, 0);
            MemoryMarshal.Write(_controls.Mapping.Bytes.Slice(row.Index * 64, 64), in control);
            if (input.Controls is { } controls)
            {
                controls.CopyTo(MemoryMarshal.Cast<byte, float>(_inputs.Mapping.Bytes)[inputs..]);
                inputs += controls.Length;
            }
            var root = new RootCorrection(new(input.RootCorrection.Translation, input.ExtractRootMotion ? 1 : 0), input.RootCorrection.Rotation);
            MemoryMarshal.Write(_roots.Mapping.Bytes.Slice(row.Index * 32, 32), in root);
        }
        if (_order.Count > 0) { _controls.Mapping.Flush(new(0, (ulong)_order.Count * 64)); _roots.Mapping.Flush(new(0, (ulong)_order.Count * 32)); }
        if (inputs > 0) _inputs.Mapping.Flush(new(0, (ulong)inputs * 4));
    }
    private void Rebuild()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        var definitions = new GpuPoseDefinitions(writer);
        int bones = 0, states = 0, locals = 0;
        foreach (SourceRow row in _order)
        {
            GpuPoseTarget input = row.Input;
            row.Range = new(bones, input.Skeleton.Skeleton.BoneCount); bones += row.Range.Count;
            row.BlendStateStart = states; row.BlendLocalStart = locals;
            int steps = input.Blend?.Steps.Length ?? 0; states += steps; locals += steps * row.Range.Count;
            row.Clip = input.Clip is { } clip ? definitions.WriteClip(clip) : uint.MaxValue;
            row.Machine = input.StateMachine is { } machine ? definitions.WriteMachine(machine) : uint.MaxValue;
            row.Blend = input.Blend is { } blend ? definitions.WriteBlend(blend) : uint.MaxValue;
            row.Reference = definitions.WriteReference(input.Skeleton.Skeleton); row.Reset = true;
        }
        _boneCount = bones;
        bytes.GetBuffer().AsSpan(0, (int)bytes.Length).CopyTo(_definitions.Mapping.Bytes);
        _definitions.Mapping.Flush(new(0, (ulong)bytes.Length));
        _controlView?.Dispose();
        foreach (BufferSrv view in _workViews) view.Dispose();
        _workViews.Clear(); _sampleBindings.Clear(); _workCounts.Clear();
        if (_order.Count == 0) return;
        _controlView = _backend.CreateBufferSrv(_device, new(_controls.Buffer, new(0, (ulong)_order.Count * 64), StructureStride: 64));
        _bindings["GpuPoseControls"] = [ResourceBinding.ReadOnlyBuffer(_controlView)];
        _advanceBindings = OrderBindings(_layouts[0], _bindings);
        var levels = new List<List<BoneWork>>();
        var sections = _order.ToDictionary(row => row.Input.Skeleton);
        foreach (SourceRow row in _order) row.Depths = new int[row.Range.Count];
        foreach (SourceRow row in _order)
            for (int bone = 0; bone < row.Range.Count; bone++)
            {
                int parent = row.Input.Skeleton.Skeleton.Parents[bone];
                SourceRow parentRow = row;
                int inherited = row.Input.Skeleton.Skeleton.InheritedParents[bone];
                if (parent < 0 && inherited >= 0)
                {
                    BoneSource source = row.Input.Skeleton.BoneSources[inherited];
                    parentRow = sections[source.Instance]; parent = source.Bone;
                }
                int depth = parent < 0 ? 0 : parentRow.Depths[parent] + 1;
                row.Depths[bone] = depth;
                while (levels.Count <= depth) levels.Add([]);
                levels[depth].Add(new((uint)row.Index, (uint)bone,
                    parent < 0 ? uint.MaxValue : (uint)(parentRow.Range.Start + parent), (uint)(row.Range.Start + bone)));
            }
        int offset = 0;
        foreach (List<BoneWork> level in levels)
        {
            ReadOnlySpan<byte> work = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(level));
            work.CopyTo(_work.Mapping.Bytes[offset..]);
            BufferSrv view = _backend.CreateBufferSrv(_device, new(_work.Buffer, new((ulong)offset, (ulong)work.Length), StructureStride: 16));
            _workViews.Add(view); _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(view)];
            _sampleBindings.Add(OrderBindings(_layouts[1], _bindings)); _workCounts.Add(level.Count); offset += work.Length;
        }
        if (offset > 0) _work.Mapping.Flush(new(0, (ulong)offset));
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Control(uint ClipOffset, float SampleTime, uint Flags, uint Starts, ulong InputNumber,
        double CalculationTime, uint MachineOffset, uint InputOffset, float PlaybackTime, uint BlendStart,
        uint ReferenceOffset, float PresentationAmount, uint Padding0, uint Padding1);
    private readonly record struct RootCorrection(Vector4 Translation, Quaternion Rotation);
    private readonly record struct BoneWork(uint Section, uint Bone, uint Parent, uint Output);
    private sealed class SourceRow(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal int Index, BlendStateStart, BlendLocalStart;
        internal uint Clip, Machine, Blend, Reference;
        internal PoseRange Range;
        internal GpuPoseTarget Input, Accepted;
        internal ulong Binding, AcceptedBinding;
        internal bool HasInput, Reset;
        internal int[] Depths = [];
    }
}
