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
    internal JobHandle ScheduleEvaluate(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        JobHandle members = scheduler.Schedule(new CaptureEntities(this),
            [.. _queryAccesses, JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
        var writes = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(this)) };
        foreach (UploadBuffer upload in _uploads)
            writes.Add(JobResourceAccess.Write(scheduler.GetResourceToken(upload.Buffer), 0, (long)upload.Buffer.Info.Size));
        if (_physics is not null) writes.AddRange(_physics.CaptureAccesses);
        JobHandle captured = _resources.ScheduleEntities(_world, _targets, new SourceEntities(this),
            new CaptureInputs(this, context), writes.ToArray(), members);
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
    private readonly struct CaptureInputs(DevicePoseSources sources, PhysicsStepContext context) : IJob
    {
        public void Execute() => sources.Capture(in context);
    }
    private readonly struct AcceptInputs(DevicePoseSources sources) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = WorldJobs.GetScheduler(sources._world);
            var commands = new JobCommandBuffer(sources._world, 1);
            commands.Schedule(0, new ApplyInputs(sources), [JobResourceAccess.Write(scheduler.GetResourceToken(sources))]);
            JobHandle applied = commands.SchedulePlayback();
            scheduler.ScheduleFinally(new ReleasePoseCommands(commands), default, applied);
        }
    }
    private readonly struct ApplyInputs(DevicePoseSources sources) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            foreach (SourceRow source in sources._order)
            {
                GpuPoseSourceState state = source.State;
                state.Input = source.Input; state.BindingVersion = source.Binding;
                if (source.Evaluate && source.HasPhysics) state.PhysicsTime = source.Input.CalculationTime;
                state.HasInput = true; source.Reset = false;
                commands.Replace(source.Entity, in state);
            }
        }
    }
    private void Capture(in PhysicsStepContext context)
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
                source.State = row.Read<GpuPoseSourceState>();
                if (source.Index != current._order.Count || !ReferenceEquals(input.Skeleton, source.State.Input.Skeleton)
                    || !ReferenceEquals(input.Clip, source.State.Input.Clip) || !ReferenceEquals(input.StateMachine, source.State.Input.StateMachine)
                    || !ReferenceEquals(input.Blend, source.State.Input.Blend) || !ReferenceEquals(input.Rig, source.State.Input.Rig)
                    || source.State.BindingVersion != input.Skeleton.BindingVersion) rebuild = true;
                source.Input = input; source.Binding = input.Skeleton.BindingVersion;
                source.Index = current._order.Count; current._order.Add(source);
            }
        });
        if (rebuild) Rebuild();
        int inputs = 0;
        foreach (SourceRow row in _order)
        {
            GpuPoseTarget input = row.Input;
            bool reset = row.Reset || !row.State.HasInput || row.State.Input.Starts != input.Starts
                || row.State.Input.AnimationStarts != input.AnimationStarts;
            bool advance = reset || row.State.Input.InputNumber != input.InputNumber;
            row.Evaluate = !row.HasPhysics || advance && (input.Playing || reset);
            uint flags = (row.Evaluate ? 1u : 0u) | 4u | (reset ? 2u | 32u | 512u : 0u) | (input.Playing ? 16u : 0u)
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
            var offset = new Vector4(input.ModelOffset, 0);
            MemoryMarshal.Write(_offsets.Mapping.Bytes.Slice(row.Index * 16, 16), in offset);
            if (row.HasPhysics)
            {
                Matrix4x4.Invert(input.ModelToWorld, out Matrix4x4 inverse);
                float delta = reset ? 0 : (float)(input.CalculationTime - row.State.PhysicsTime);
                var frame = new PhysicsFrame(input.ModelToWorld, inverse, delta, reset ? 1u : 0u,
                    (uint)context.Substep, 0, context.Number, input.CalculationTime);
                MemoryMarshal.Write(_physicsFrames!.Mapping.Bytes.Slice(row.Index * 160, 160), in frame);
            }
        }
        if (_order.Count > 0) { _controls.Mapping.Flush(new(0, (ulong)_order.Count * 64)); _roots.Mapping.Flush(new(0, (ulong)_order.Count * 32)); }
        if (_order.Count > 0) _offsets.Mapping.Flush(new(0, (ulong)_order.Count * 16));
        if (inputs > 0) _inputs.Mapping.Flush(new(0, (ulong)inputs * 4));
        if (_physicsFrames is not null && _order.Count > 0) _physicsFrames.Mapping.Flush(new(0, (ulong)_order.Count * 160));
        foreach (SourceRow row in _order)
        {
            GpuPoseMovement movement = row.Input.Movement;
            var value = new Movement(new(movement.Velocity, movement.Weight), new(movement.AnimationVelocity, 0), new(movement.FacingDirection, 0));
            MemoryMarshal.Write(_movements.Mapping.Bytes.Slice(row.Index * 48, 48), in value);
        }
        if (_order.Count > 0) _movements.Mapping.Flush(new(0, (ulong)_order.Count * 48));
        _world.ExecuteReadQuery(_targets, ref sources, static (scoped QueryCursor rows, ref DevicePoseSources current) =>
        {
            foreach (SourceRow source in current._order)
            {
                if (source.Input.Rig?.GoalCount is not > 0) continue;
                rows.TryGetRow(source.Entity, out QueryRow row);
                ReadOnlySpan<GpuPoseGoal> goals = row.ReadBuffer<GpuPoseGoal>().AsSpan();
                MemoryMarshal.AsBytes(goals).CopyTo(current._goals.Mapping.Bytes[(source.GoalStart * 48)..]);
            }
        });
        int goalCount = _order.Sum(row => row.Input.Rig?.GoalCount ?? 0);
        if (goalCount > 0) _goals.Mapping.Flush(new(0, (ulong)goalCount * 48));
    }
    private void Rebuild()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        var definitions = new GpuPoseDefinitions(writer);
        int bones = 0, states = 0, locals = 0, goals = 0;
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
            row.GoalStart = goals; goals += input.Rig?.GoalCount ?? 0;
            row.HasPhysics = false;
            if (input.Rig is { } rig) foreach (GpuPoseSource step in rig.Steps) row.HasPhysics |= step is GpuXpbdPose;
        }
        _boneCount = bones;
        _controlView?.Dispose();
        foreach (BufferSrv view in _workViews) view.Dispose();
        _workViews.Clear(); _sampleBindings.Clear(); _rigOperations.Clear(); _workCounts.Clear();
        if (_order.Count == 0) return;
        _controlView = _backend.CreateBufferSrv(_device, new(_controls.Buffer, new(0, (ulong)_order.Count * 64), StructureStride: 64));
        _bindings["GpuPoseControls"] = [ResourceBinding.ReadOnlyBuffer(_controlView)];
        _advanceBindings = OrderBindings(_layouts[0], _bindings);
        var levels = new List<List<BoneWork>>();
        var children = new List<int>[_boneCount];
        for (int bone = 0; bone < children.Length; bone++) children[bone] = [];
        var sections = _order.ToDictionary(row => row.Input.Skeleton);
        foreach (SourceRow row in _order)
        { row.Depths = new int[row.Range.Count]; Array.Fill(row.Depths, -1); }
        foreach (SourceRow row in _order)
            for (int bone = 0; bone < row.Range.Count; bone++)
            {
                (SourceRow parentRow, int parent) = Parent(row, bone);
                int depth = Depth(row, bone);
                if (parent >= 0) children[parentRow.Range.Start + parent].Add(row.Range.Start + bone);
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
        WriteRigOperations(writer, children, ref offset);
        bytes.GetBuffer().AsSpan(0, (int)bytes.Length).CopyTo(_definitions.Mapping.Bytes);
        _definitions.Mapping.Flush(new(0, (ulong)bytes.Length));
        if (offset > 0) _work.Mapping.Flush(new(0, (ulong)offset));

        (SourceRow Section, int Bone) Parent(SourceRow row, int bone)
        {
            Skeleton skeleton = row.Input.Skeleton.Skeleton;
            int parent = skeleton.Parents[bone], inherited = skeleton.InheritedParents[bone];
            if (parent >= 0 || inherited < 0) return (row, parent);
            BoneSource source = row.Input.Skeleton.BoneSources[inherited];
            return (sections[source.Instance], source.Bone);
        }
        int Depth(SourceRow row, int bone)
        {
            if (row.Depths[bone] >= 0) return row.Depths[bone];
            (SourceRow parentRow, int parent) = Parent(row, bone);
            return row.Depths[bone] = parent < 0 ? 0 : Depth(parentRow, parent) + 1;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Control(uint ClipOffset, float SampleTime, uint Flags, uint Starts, ulong InputNumber,
        double CalculationTime, uint MachineOffset, uint InputOffset, float PlaybackTime, uint BlendStart,
        uint ReferenceOffset, float PresentationAmount, uint Padding0, uint Padding1);
    private readonly record struct RootCorrection(Vector4 Translation, Quaternion Rotation);
    private readonly record struct Movement(Vector4 Velocity, Vector4 AnimationVelocity, Vector4 FacingDirection);
    private readonly record struct PhysicsFrame(Matrix4x4 ModelToWorld, Matrix4x4 WorldToModel, float Duration, uint Reset,
        uint Substep, uint Padding, long Number, double Time);
    private readonly record struct BoneWork(uint Section, uint Bone, uint Parent, uint Output);
    private readonly record struct RigOperation(int Kernel, ResourceBinding[] Bindings, int Count);
    private sealed class SourceRow(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal int Index, BlendStateStart, BlendLocalStart, GoalStart;
        internal uint Clip, Machine, Blend, Reference;
        internal PoseRange Range;
        internal GpuPoseTarget Input;
        internal GpuPoseSourceState State;
        internal ulong Binding;
        internal bool Reset, HasPhysics, Evaluate;
        internal int[] Depths = [];
    }
}
