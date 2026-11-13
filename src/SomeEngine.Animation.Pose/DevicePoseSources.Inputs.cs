using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
    internal JobHandle ScheduleEvaluate(in PhysicsStepContext context, JobHandle dependency, int targetKernel = -1, bool commonBodies = true)
    {
        if (targetKernel == -1 && _physics is not null) return ScheduleChains(in context, dependency);
        Scheduler scheduler = _world.Execution!.Scheduler!;
        JobHandle reset = scheduler.Schedule(new ResetCapture(this),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
        JobHandle captured = _runner.Schedule(_targets, new CaptureRows(this), reset, parallel: false);
        return scheduler.Schedule(new ScheduleInputCapture(this, context, targetKernel, commonBodies),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)), .. ((WorldJobs)_world.Execution!).StructureRead], captured);
    }
    private readonly struct ResetCapture(DevicePoseSources sources) : IJob
    {
        public void Execute()
        {
            sources._entityCount = 0; sources._captureAdded = false;
            foreach (SourceRow source in sources._sources.Values) source.Captured = false;
            if (sources._selectedEntities is { } selected)
            {
                selected.Span.CopyTo(sources._entities);
                sources._entityCount = selected.Length;
            }
        }
    }
    private readonly struct CaptureRows(DevicePoseSources sources) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<GpuPoseTarget>(hasResources: true), ChunkAccess.Read<GpuPoseSourceState>(hasResources: true),
                ChunkAccess.ReadOptionalBuffer<GpuPoseGoal>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(sources);
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<GpuPoseTarget> inputs = rows.Read<GpuPoseTarget>();
            ReadOnlySpan<GpuPoseSourceState> states = rows.Read<GpuPoseSourceState>();
            for (int row = 0; row < rows.Count; row++)
            {
                Entity entity = rows.Entities[row];
                if (!sources._sources.TryGetValue(entity, out SourceRow? source))
                {
                    sources._sources.Add(entity, source = new(entity)); sources._captureAdded = true;
                }
                source.Input = inputs[row]; source.State = states[row]; source.Captured = true;
                if (sources._selectedEntities is null) sources._entities[sources._entityCount++] = entity;
                int goals = inputs[row].Rig?.GoalCount ?? 0;
                if (source.Goals.Length != goals) source.Goals = new GpuPoseGoal[goals];
                if (goals != 0) rows.ReadBuffer<GpuPoseGoal>(row).AsSpan().CopyTo(source.Goals);
            }
        }
    }
    private readonly struct ScheduleInputCapture(DevicePoseSources sources, PhysicsStepContext context, int targetKernel, bool commonBodies) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = sources._world.Execution!.Scheduler!;
            var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(scheduler.GetResourceToken(sources)) };
            foreach (UploadBuffer upload in sources._uploads)
                accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(upload.Buffer), 0, (long)upload.Buffer.Info.Size));
            var resources = new JobResourceAccessBuilder(scheduler, accesses);
            for (int index = 0; index < sources._entityCount; index++)
                if (sources._sources.TryGetValue(sources._entities[index], out SourceRow? source) && source.Captured)
                    GpuPoseTargetResources.Collect(in source.Input, ref resources);
            if (sources._physics is { } physics) accesses.AddRange(physics.CaptureAccesses);
            JobHandle captured = scheduler.Schedule(new CaptureInputs(sources, context, targetKernel >= 0 && commonBodies), accesses.ToArray());
            scheduler.Schedule(new SubmitSources(sources, targetKernel), [JobResourceAccess.Write(scheduler.GetResourceToken(sources))], captured);
        }
    }
    private readonly struct CaptureInputs(DevicePoseSources sources, PhysicsStepContext context, bool commonBodies) : IJob
    {
        public void Execute() => sources.Capture(in context, commonBodies);
    }
    private readonly struct SubmitSources(DevicePoseSources sources, int targetKernel) : IJob
    {
        public void Execute()
        {
            Scheduler scheduler = sources._world.Execution!.Scheduler!;
            JobHandle complete = DevicePhysicsJobs.Schedule(scheduler, new BodySubmission(sources, targetKernel, -1), sources.GetDeviceAccesses(scheduler, false), default);
            sources._runner.Schedule(sources._targets, new AcceptInputs(sources), complete, parallel: false);
        }
    }
    private readonly struct AcceptInputs(DevicePoseSources sources) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Write<GpuPoseSourceState>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(sources);
        public void Execute(SystemChunk rows)
        {
            sources.ReleaseStateCopies();
            for (int row = 0; row < rows.Count; row++)
            {
                if (!sources._sources.TryGetValue(rows.Entities[row], out SourceRow? source) || !source.Captured) continue;
                GpuPoseSourceState state = source.State;
                state.Sources = sources; state.Range = source.Range; state.PlaybackIndex = source.Index;
                state.BlendStateStart = source.BlendStateStart; state.BlendLocalStart = source.BlendLocalStart;
                state.Input = source.Input; state.BindingVersion = source.Binding;
                state.CpuBones = source.Input.Skeleton.CpuBones; state.SelectedBones = source.SelectedBones;
                state.Evaluation = source.Input.Evaluation; state.Presentation = source.Presentation;
                state.HasSamples |= source.Sample; source.SelectionChanged = false;
                if (source.HasPhysics) state.PhysicsTime = source.CalculationTime;
                state.HasInput = true; source.Reset = false;
                rows.Write<GpuPoseSourceState>(row) = state;
            }
        }
    }
    private void Capture(in PhysicsStepContext context, bool commonBodies)
    {
        bool rebuild = _captureAdded || _order.Count != _entityCount, evaluationChanged = false;
        _order.Clear();
            for (int index = 0; index < _entityCount; index++)
            {
                Entity entity = _entities[index];
                if (!_sources.TryGetValue(entity, out SourceRow? source) || !source.Captured) continue;
                GpuPoseTarget input = source.Input;
                evaluationChanged |= !ReferenceEquals(source.State.Evaluation, input.Evaluation);
                bool changed = !source.State.HasInput || !ReferenceEquals(input.Skeleton, source.State.Input.Skeleton)
                    || !ReferenceEquals(input.Clip, source.State.Input.Clip) || !ReferenceEquals(input.StateMachine, source.State.Input.StateMachine)
                    || !ReferenceEquals(input.Blend, source.State.Input.Blend) || !ReferenceEquals(input.Rig, source.State.Input.Rig)
                    || source.State.BindingVersion != input.Skeleton.BindingVersion
                    || !source.State.CpuBones.Equals(input.Skeleton.CpuBones);
                source.Reset |= changed || source.State.Input.Starts != input.Starts || source.State.Input.AnimationStarts != input.AnimationStarts;
                if (source.Index != _order.Count || changed) rebuild = true;
                source.Input = input; source.Binding = input.Skeleton.BindingVersion;
                source.Index = _order.Count; _order.Add(source);
            }
        foreach (Entity removed in _sources.Keys.Except(_order.Select(row => row.Entity)).ToArray()) _sources.Remove(removed);
        if (rebuild || evaluationChanged) Rebuild();
        int inputs = 0;
        foreach (SourceRow row in _order)
        {
            GpuPoseTarget input = row.Input;
            bool reset = row.Reset || !row.State.HasInput || row.State.Input.Starts != input.Starts
                || row.State.Input.AnimationStarts != input.AnimationStarts;
            bool advance = reset || row.State.Input.InputNumber != input.InputNumber;
            row.CalculationTime = commonBodies ? context.EndTime : ReadCalculationTime(row);
            bool dependencies = row.CalculationTime > row.State.PhysicsTime;
            row.CommonBodies = commonBodies;
            row.Evaluate = !row.HasPhysics || (advance || dependencies || commonBodies) && (input.Playing || reset);
            bool resetSamples = reset || row.SelectionChanged || !row.State.HasSamples
                || !ReferenceEquals(row.State.Evaluation, input.Evaluation);
            float interval = input.Evaluation?.Interval ?? 0;
            row.Sample = row.Evaluate && (resetSamples || advance && input.CalculationTime >= row.State.Presentation.CurrentTime + interval);
            double current = row.Sample ? input.CalculationTime : row.State.Presentation.CurrentTime;
            double previous = row.Sample ? resetSamples ? current : row.State.Presentation.CurrentTime : row.State.Presentation.PreviousTime;
            var identity = new GpuPoseIdentity(row.Entity, input.Skeleton, input.Starts, input.InputNumber, input.AnimationStarts);
            row.Presentation = row.Evaluate ? GpuPosePresentation.FromSampleTimes(previous, current, row.CalculationTime, interval) with
            {
                PreviousInput = row.Sample ? resetSamples ? identity : row.State.Presentation.CurrentInput : row.State.Presentation.PreviousInput,
                CurrentInput = row.Sample ? identity : row.State.Presentation.CurrentInput,
            } : row.State.Presentation;
            uint flags = (row.Evaluate ? 1u : 0u) | 4u | (reset ? 2u | 32u : 0u) | (input.Playing ? 16u : 0u)
                | (input.StateMachine is not null ? 8u : 0u) | (input.Blend is not null ? 64u : 0u)
                | (advance ? 128u : 0u) | (row.Sample ? 256u : 0u) | (resetSamples ? 512u : 0u);
            var control = new Control(input.Blend is null ? row.Clip : (uint)row.BlendLocalStart, input.SampleTime,
                flags, input.Starts, input.InputNumber, input.CalculationTime,
                input.Blend is null ? row.Machine : row.Blend, (uint)inputs, input.PlaybackTime,
                (uint)row.BlendStateStart, row.Reference, row.Presentation.Amount, 0, 0);
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
                float delta = commonBodies ? (float)context.Duration : reset ? 0 : (float)(row.CalculationTime - row.State.PhysicsTime);
                float targetDuration = (float)Math.Max(context.Duration, row.CalculationTime - context.StartTime);
                var frame = new PhysicsFrame(input.ModelToWorld, inverse, delta, reset ? 1u : 0u,
                    (uint)context.Substep, targetDuration, context.Number, row.CalculationTime);
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
        foreach (SourceRow source in _order)
            if (source.Goals.Length != 0)
                MemoryMarshal.AsBytes(source.Goals.AsSpan()).CopyTo(_goals.Mapping.Bytes[(source.GoalStart * 48)..]);
        int goalCount = _order.Sum(row => row.Input.Rig?.GoalCount ?? 0);
        if (goalCount > 0) _goals.Mapping.Flush(new(0, (ulong)goalCount * 48));
        CaptureCpuModels();
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
            row.GoalStart = goals; goals += input.Rig?.GoalCount ?? 0;
            row.HasPhysics = false;
            if (input.Rig is { } rig) foreach (GpuPoseSource step in rig.Steps) row.HasPhysics |= step is GpuXpbdPose;
        }
        AddCpuSections(ref bones);
        ReadOnlyMemory<bool>[] before = _order.Select(row => row.SelectedBones.Length > 0 ? row.SelectedBones.AsMemory() : row.State.SelectedBones).ToArray();
        foreach (SourceRow row in _order)
        {
            row.SelectedBones = new bool[row.Range.Count];
            GpuPoseEvaluation.SelectBones(row.SelectedBones, row.Input.Evaluation, row.Input.Skeleton.CpuBones.Span, row.Input.Rig);
        }
        foreach (SourceRow row in _order)
            for (int bone = 0; bone < row.Range.Count; bone++)
                if (row.SelectedBones[bone]) RequireParents(row, bone);
        for (int index = 0; index < _order.Count; index++)
        {
            SourceRow row = _order[index];
            row.SelectionChanged |= !row.SelectedBones.AsSpan().SequenceEqual(before[index].Span);
            row.Reference = definitions.WriteReference(row.Input.Skeleton.Skeleton, row.SelectedBones);
        }
        _boneCount = bones;
        CreateStateCopies();
        _controlView?.Dispose();
        foreach (BufferSrv view in _workViews) view.Dispose();
        _workViews.Clear(); _sampleBindings.Clear(); _rigOperations.Clear(); _workCounts.Clear();
        if (_order.Count == 0) return;
        _controlView = _backend.CreateBufferSrv(_device, new(_controls.Buffer, new(0, (ulong)_order.Count * 64), StructureStride: 64));
        _bindings["GpuPoseControls"] = [ResourceBinding.ReadOnlyBuffer(_controlView)];
        var levels = new List<List<BoneWork>>();
        var children = new List<int>[_boneCount];
        for (int bone = 0; bone < children.Length; bone++) children[bone] = [];

        foreach (SourceRow row in _order)
        { row.Depths = new int[row.Range.Count]; Array.Fill(row.Depths, -1); }
        foreach (SourceRow row in _order)
            for (int bone = 0; bone < row.Range.Count; bone++)
            {
                (SourceRow? parentRow, int parent, int parentIndex) = Parent(row, bone);
                if (parentIndex >= 0) children[parentIndex].Add(row.Range.Start + bone);
                if (CpuWritten(row, bone)) continue;
                int depth = Depth(row, bone);
                while (levels.Count <= depth) levels.Add([]);
                levels[depth].Add(new((uint)row.Index, (uint)bone,
                    parentIndex < 0 ? uint.MaxValue : (uint)parentIndex, (uint)(row.Range.Start + bone)));
            }
        int offset = 0;
        foreach (List<BoneWork> level in levels)
        {
            ReadOnlySpan<byte> work = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(level));
            work.CopyTo(_work.Mapping.Bytes[offset..]);
            BufferSrv view = _backend.CreateBufferSrv(_device, new(_work.Buffer, new((ulong)offset, (ulong)work.Length), StructureStride: 16));
            _workViews.Add(view); _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(view)];
            _workCounts.Add(level.Count); offset += work.Length;
        }
        WriteRigOperations(writer, children, ref offset);
        WriteDefinitions(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
        _advanceBindings = OrderBindings(_layouts[0], _bindings);
        for (int level = 0; level < _workCounts.Count; level++)
        {
            _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(_workViews[level])];
            _sampleBindings.Add(OrderBindings(_layouts[1], _bindings));
        }
        for (int index = 0; index < _rigOperations.Count; index++)
        {
            RigOperation operation = _rigOperations[index];
            _bindings["GpuPoseWork"] = [ResourceBinding.ReadOnlyBuffer(operation.Work)];
            _rigOperations[index] = operation with { Bindings = OrderBindings(_layouts[operation.Kernel], _bindings) };
        }
        if (offset > 0) _work.Mapping.Flush(new(0, (ulong)offset));

        int Depth(SourceRow row, int bone)
        {
            if (row.Depths[bone] >= 0) return row.Depths[bone];
            (SourceRow? parentRow, int parent, int _) = Parent(row, bone);
            return row.Depths[bone] = parentRow is null || CpuWritten(parentRow, parent) ? 0 : Depth(parentRow, parent) + 1;
        }
        void RequireParents(SourceRow row, int bone)
        {
            (SourceRow? parentRow, int parent, int _) = Parent(row, bone);
            if (parentRow is null) return;
            parentRow.SelectedBones[parent] = true;
            RequireParents(parentRow, parent);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Control(uint ClipOffset, float SampleTime, uint Flags, uint Starts, ulong InputNumber,
        double CalculationTime, uint MachineOffset, uint InputOffset, float PlaybackTime, uint BlendStart,
        uint ReferenceOffset, float PresentationAmount, uint Padding0, uint Padding1);
    private readonly record struct RootCorrection(Vector4 Translation, Quaternion Rotation);
    private readonly record struct Movement(Vector4 Velocity, Vector4 AnimationVelocity, Vector4 FacingDirection);
    private readonly record struct PhysicsFrame(Matrix4x4 ModelToWorld, Matrix4x4 WorldToModel, float Duration, uint Reset,
        uint Substep, float TargetDuration, long Number, double Time);
    private readonly record struct BoneWork(uint Section, uint Bone, uint Parent, uint Output);
    private readonly record struct RigOperation(int Kernel, BufferSrv Work, ResourceBinding[] Bindings, int Count, bool AfterPhysics);
    private sealed class SourceRow(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal int Index, BlendStateStart, BlendLocalStart, GoalStart;
        internal uint Clip, Machine, Blend, Reference;
        internal PoseRange Range;
        internal GpuPoseTarget Input;
        internal GpuPoseSourceState State;
        internal ulong Binding;
        internal bool Reset, HasPhysics, Evaluate, CommonBodies, Sample, SelectionChanged, Captured;
        internal GpuPoseGoal[] Goals = [];
        internal GpuPosePresentation Presentation;
        internal double CalculationTime;
        internal bool[] SelectedBones = [];
        internal int[] Depths = [];
    }
}
