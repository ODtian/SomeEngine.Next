using System.Runtime.InteropServices;
using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Selects the device representation of this body's original activity component.</summary>
public readonly record struct DeviceBodyActivityBinding(DeviceBodyActivities Activities) : ITrackedComponent;

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct DeviceBodyActivityState
{
    public uint EntityIndex, EntityGeneration, Flags;
    public float QuietSeconds, Radius;
    public int Substep;
    public long Number;
}

/// <summary>Activity history on the original body indices, with native input versions kept at this resource.</summary>
public sealed partial class DeviceBodyActivities : IDisposable
{
    private static readonly TraceSite CaptureInputsTrace = new("physics", "Activity.CaptureInputs");
    private static readonly TraceSite SourceRowsTrace = new("physics", "Activity.ReadSources");
    internal readonly IGraphicsBackend Backend;
    internal readonly Device Device;
    internal readonly DeviceBodies Bodies;
    internal readonly World World;
    internal readonly Buffer States, Inputs;
    internal readonly Query SourceRows, ResultRows;
    private readonly SystemRunner _runner;
    internal readonly DeviceBodyActivityState[] Results;
    private readonly BufferUav _statesView;
    private readonly BufferSrv _inputsView, _resultSelectionView;
    private readonly Buffer _resultSelection;
    private readonly MappedBuffer _resultMapping;
    private List<int> _selectedResults = [], _nextSelectedResults = [];
    private readonly List<int> _newResults = [];
    private ulong _resultMembership;
    private bool _hasResultSelection, _changeResultSelection, _resultMaskChanged;
    private readonly CpuVersion[] _versions;
    private readonly Query _wakeRows, _activityMembers;
    private readonly List<int> _transientInputs = [];
    private MappedBuffer _inputMapping;
    private readonly PhysicsQueryWindow _window;
    private ulong _resultBaseline, _sourceObserved;
    private ulong _membershipVersion, _activityMembershipVersion, _lastSystemVersion;
    private bool _readAll;

    public DeviceBodyActivities(IGraphicsBackend backend, Device device, World world, Query members, DeviceBodies bodies, Query? resultMembers = null)
    {
        Backend = backend; Device = device; World = world; Bodies = bodies;
        Results = new DeviceBodyActivityState[bodies.Capacity];
        _versions = new CpuVersion[bodies.Capacity];
        States = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 32,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Body activity states"), MemoryType.DeviceLocal);
        Inputs = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 32, BufferUsages.ShaderRead,
            "Body activity inputs"), MemoryType.Upload);
        _statesView = backend.CreateBufferUav(device, new(States, new(0, States.Info.Size), StructureStride: 32));
        _inputsView = backend.CreateBufferSrv(device, new(Inputs, new(0, Inputs.Info.Size), StructureStride: 32));
        _resultSelection = backend.CreateBuffer(device, new((ulong)bodies.Capacity * 4,
            BufferUsages.ShaderRead, "CPU activity result selection"), MemoryType.Upload);
        _resultSelectionView = backend.CreateBufferSrv(device, new(_resultSelection, BufferRange.Whole, StructureStride: 4));
        _resultMapping = backend.Map(_resultSelection, MapType.Write, BufferRange.Whole);
        _resultMapping.Bytes.Clear();
        _inputMapping = backend.Map(Inputs, MapType.Write, BufferRange.Whole);
        _inputMapping.Bytes.Clear();
        SourceRows = SystemRunner.Bind<ReadSources>(members);
        ResultRows = SystemRunner.Bind<SelectCpuResults>(resultMembers ?? members);
        _resultMembership = ResultRows.MembershipVersion;
        _window = new(SourceRows); _runner = new(world);
        _wakeRows = world.Query(new QueryDescription().With<DeviceBodyBinding>().With<SomeEngine.ECS.Buffer<BodyWake>>());
        _activityMembers = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>()
            .WithStored<DeviceBodyActivityBinding>().WithStored<BodyActivity>());
        _membershipVersion = SourceRows.MembershipVersion;
        _activityMembershipVersion = _activityMembers.MembershipVersion;

    }

    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceBodyActivities", [ResourceBinding.WritableBuffer(_statesView)]);
        bindings.Add("DeviceBodyActivityInputs", [ResourceBinding.ReadOnlyBuffer(_inputsView)]);
        bindings.Add("DeviceBodyActivityCpuResults", [ResourceBinding.ReadOnlyBuffer(_resultSelectionView)]);
        Bodies.AddCpuInputBindings(bindings);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_resultSelection), 0, (long)_resultSelection.Info.Size),
            .. Bodies.GetCpuInputReadAccesses(scheduler)];

    public JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(States), 0, (long)States.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(this)),
            JobResourceAccess.Read(scheduler.GetResourceToken(_resultSelection), 0, (long)_resultSelection.Info.Size),
            .. Bodies.GetCpuInputReadAccesses(scheduler)];
    internal JobResourceAccess[] GetResultReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_resultSelection), 0, (long)_resultSelection.Info.Size)];

    internal JobHandle ScheduleCapture(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = World.Execution!.Scheduler!;
        JobResourceAccess[] resetAccesses = [.. ((WorldJobs)World.Execution!).StructureRead,
            JobResourceAccess.Write(scheduler.GetResourceToken(this)), JobResourceAccess.Write(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size)];
        JobHandle reset = scheduler.Schedule(new ResetInputs(this, context), resetAccesses, dependency);
        JobHandle sources = _runner.Schedule(SourceRows.Since(_window.LastVersion), new ReadSources(this), reset);
        JobHandle gathered = _runner.Schedule(SourceRows.Since(_window.LastVersion), new GatherInputChanges(this), sources, parallel: false);
        JobHandle wakes = _runner.Schedule(_wakeRows, new ReadWakes(this, context.StartTime, context.EndTime), gathered, parallel: false);
        JobHandle checkedResults = _runner.Schedule(ResultRows.Since(_window.LastVersion), new CheckCpuResultSelection(this), wakes, parallel: false);
        JobHandle resetResults = scheduler.Schedule(new ResetCpuResultMask(this),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)),
                JobResourceAccess.Write(scheduler.GetResourceToken(_resultSelection), 0, (long)_resultSelection.Info.Size)], checkedResults);
        JobHandle results = _runner.Schedule(ResultRows.Since(_window.LastVersion), new SelectCpuResults(this), resetResults, parallel: false);
        return scheduler.Schedule(new FinishInputs(this),
            [JobResourceAccess.Write(scheduler.GetResourceToken(this)), JobResourceAccess.Write(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size),
                JobResourceAccess.Write(scheduler.GetResourceToken(_resultSelection), 0, (long)_resultSelection.Info.Size)], results);
    }
    private readonly struct ResetInputs(DeviceBodyActivities activity, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            activity._resultBaseline = activity._window.Begin(context);
            activity._sourceObserved = activity._lastSystemVersion;
            ulong resultMembership = activity.ResultRows.MembershipVersion;
            activity._changeResultSelection = !activity._hasResultSelection || activity.ResultRows.HasVersionFilters
                || activity._resultMembership != resultMembership;
            activity._resultMembership = resultMembership;
            ulong membership = activity.SourceRows.MembershipVersion;
            ulong activityMembership = activity._activityMembers.MembershipVersion;
            activity._readAll = activity._lastSystemVersion == 0 || membership != activity._membershipVersion
                || activityMembership != activity._activityMembershipVersion;
            if (activity._readAll) activity._inputMapping.Bytes.Clear();
            else
            {
                Span<ActivityInput> inputs = MemoryMarshal.Cast<byte, ActivityInput>(activity._inputMapping.Bytes);
                foreach (int index in activity._transientInputs) inputs[index].Flags &= 3;
            }
            activity._transientInputs.Clear();
            activity._membershipVersion = membership; activity._activityMembershipVersion = activityMembership;
        }
    }
    private readonly struct FinishInputs(DeviceBodyActivities activity) : IJob
    {
        public void Execute()
        {
            activity._inputMapping.Flush(activity._inputMapping.Range);
            if (activity._changeResultSelection)
            {
                Span<uint> mask = MemoryMarshal.Cast<byte, uint>(activity._resultMapping.Bytes);
                foreach (int index in activity._selectedResults) if (mask[index] == 4) mask[index] = 0;
                (activity._selectedResults, activity._nextSelectedResults) = (activity._nextSelectedResults, activity._selectedResults);
            }
            if (activity._resultMaskChanged) activity._resultMapping.Flush(activity._resultMapping.Range);
            activity._lastSystemVersion = activity._sourceObserved;
            activity._window.Accept(activity._sourceObserved);
        }
    }

    private readonly struct CheckCpuResultSelection(DeviceBodyActivities activities) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true), ChunkAccess.Read<DeviceBodyActivityBinding>(hasResources: true),
                ChunkAccess.Read<BodyActivity>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(activities);
        public void Execute(SystemChunk rows)
        {
            activities._changeResultSelection |= rows.WasWritten<DeviceBodyBinding>(activities._lastSystemVersion)
                || rows.WasWritten<DeviceBodyActivityBinding>(activities._lastSystemVersion);
        }
    }
    private readonly struct ResetCpuResultMask(DeviceBodyActivities activities) : IJob
    {
        public void Execute()
        {
            Span<uint> mask = MemoryMarshal.Cast<byte, uint>(activities._resultMapping.Bytes);
            activities._resultMaskChanged = activities._changeResultSelection || activities._newResults.Count != 0;
            foreach (int index in activities._newResults) mask[index] = 1;
            activities._newResults.Clear();
            if (activities._changeResultSelection)
            {
                foreach (int index in activities._selectedResults) mask[index] = 4;
                activities._nextSelectedResults.Clear();
            }
            activities._hasResultSelection = true;
        }
    }
    private readonly struct SelectCpuResults(DeviceBodyActivities activities) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => CheckCpuResultSelection.Components;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(activities);
            resources.Write(activities._resultSelection, 0, (long)activities._resultSelection.Info.Size);
        }
        public void Execute(SystemChunk rows)
        {
            if (!activities._changeResultSelection) return;
            ReadOnlySpan<DeviceBodyBinding> bodies = rows.Read<DeviceBodyBinding>();
            ReadOnlySpan<DeviceBodyActivityBinding> bindings = rows.Read<DeviceBodyActivityBinding>();
            Span<uint> mask = MemoryMarshal.Cast<byte, uint>(activities._resultMapping.Bytes);
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceBodyBinding body = bodies[row];
                if (!ReferenceEquals(body.Bodies, activities.Bodies) || !ReferenceEquals(bindings[row].Activities, activities)) continue;
                bool selected = mask[body.Index] == 4;
                mask[body.Index] = selected ? 1u : 2u;
                activities._nextSelectedResults.Add(body.Index);
                if (!selected) activities._newResults.Add(body.Index);
            }
        }
    }

    private readonly record struct CpuVersion(Entity Entity, ulong Activity, ulong Observed = 0);
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ActivityInput
    {
        public uint EntityIndex, EntityGeneration, Flags, Sleeping;
        public float QuietSeconds, Radius;
    }
    private readonly struct ReadSources(DeviceBodyActivities activities) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true),
                ChunkAccess.ReadOptional<DeviceBodyActivityBinding>(hasResources: true), ChunkAccess.ReadOptional<BodyActivity>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Read(activities);
        }
        public void CollectRangeResources(SystemChunk rows, ref JobResourceAccessBuilder resources) =>
            activities.CollectSourceRanges(rows, ref resources, write: true);
        public void Execute(SystemChunk rows)
        {
            using var trace = EngineTrace.Begin(SourceRowsTrace);
            ulong since = activities._lastSystemVersion;
            bool bindingsChanged = activities._readAll || rows.WasWritten<DeviceBodyBinding>(since)
                || rows.WasWritten<DeviceBodyActivityBinding>(since);
            if (!bindingsChanged && !rows.WasWritten<BodyActivity>(since)) return;
            ReadOnlySpan<DeviceBodyBinding> bodies = rows.Read<DeviceBodyBinding>();
            bool hasActivity = rows.TryRead<BodyActivity>(out ReadOnlySpan<BodyActivity> values);
            bool hasBindings = rows.TryRead<DeviceBodyActivityBinding>(out ReadOnlySpan<DeviceBodyActivityBinding> bindings);
            ulong observed = activities.World.Version;
            Span<ActivityInput> inputs = MemoryMarshal.Cast<byte, ActivityInput>(activities._inputMapping.Bytes);
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceBodyBinding body = bodies[row];
                if (!ReferenceEquals(body.Bodies, activities.Bodies)) continue;
                Entity entity = rows.Entities[row];
                CpuVersion previous = activities._versions[body.Index];
                bool selected = hasActivity && rows.Has<BodyActivity>(row) && hasBindings
                    && rows.Has<DeviceBodyActivityBinding>(row) && ReferenceEquals(bindings[row].Activities, activities);
                bool written = selected && rows.WasWritten<BodyActivity>(row, previous.Activity);
                if (!activities._readAll && previous.Entity == entity && !written
                    && !rows.WasWritten<DeviceBodyBinding>(row, since)
                    && !rows.WasWritten<DeviceBodyActivityBinding>(row, since))
                {
                    activities._versions[body.Index] = previous with { Observed = observed };
                    continue;
                }
                ulong version = selected ? previous.Entity != entity || written ? observed : previous.Activity : 0;
                var current = new CpuVersion(entity, version, observed);
                var input = new ActivityInput { EntityIndex = (uint)entity.Id,
                    EntityGeneration = (uint)entity.Generation, Flags = selected ? 3u : 1u };
                if (current.Entity != previous.Entity || current.Activity != previous.Activity) input.Flags |= 4;
                if (selected)
                {
                    BodyActivity activity = values[row];
                    input.Sleeping = activity.Sleeping ? 1u : 0u;
                    input.QuietSeconds = activity.QuietSeconds; input.Radius = activity.Radius;
                }
                inputs[body.Index] = input;
                activities._versions[body.Index] = current;
            }
        }
    }
    private void CollectSourceRanges(SystemChunk rows, ref JobResourceAccessBuilder resources, bool write)
    {
        ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
        int first = 0;
        while (first < bindings.Length)
        {
            DeviceBodyBinding binding = bindings[first];
            if (!ReferenceEquals(binding.Bodies, Bodies)) { first++; continue; }
            int end = first + 1;
            while (end < bindings.Length && ReferenceEquals(bindings[end].Bodies, Bodies)
                && bindings[end].Index == binding.Index + end - first) end++;
            if (write)
            {
                resources.Write(_versions.AsMemory(binding.Index, end - first));
                resources.Write(Inputs, (long)binding.Index * 32, (long)(end - first) * 32);
            }
            else
            {
                resources.Read(_versions.AsMemory(binding.Index, end - first));
                resources.Read(Inputs, (long)binding.Index * 32, (long)(end - first) * 32);
            }
            first = end;
        }
    }
    private readonly struct GatherInputChanges(DeviceBodyActivities activities) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => ReadSources.Components;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(activities);
        public void CollectRangeResources(SystemChunk rows, ref JobResourceAccessBuilder resources) =>
            activities.CollectSourceRanges(rows, ref resources, write: false);
        public void Execute(SystemChunk rows)
        {
            ulong since = activities._lastSystemVersion;
            if (!activities._readAll && !rows.WasWritten<DeviceBodyBinding>(since)
                && !rows.WasWritten<DeviceBodyActivityBinding>(since) && !rows.WasWritten<BodyActivity>(since)) return;
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            ReadOnlySpan<ActivityInput> inputs = MemoryMarshal.Cast<byte, ActivityInput>(activities._inputMapping.Bytes);
            foreach (ref readonly DeviceBodyBinding binding in bindings)
            {
                if (!ReferenceEquals(binding.Bodies, activities.Bodies)) continue;
                activities._sourceObserved = Math.Max(activities._sourceObserved, activities._versions[binding.Index].Observed);
                if ((inputs[binding.Index].Flags & 4) != 0) activities._transientInputs.Add(binding.Index);
            }
        }
    }
    private readonly struct ReadWakes(DeviceBodyActivities activities, double start, double end) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true), ChunkAccess.ReadBuffer<BodyWake>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(activities); resources.Write(activities.Inputs, 0, (long)activities.Inputs.Info.Size);
        }
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<DeviceBodyBinding> bodies = rows.Read<DeviceBodyBinding>();
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceBodyBinding body = bodies[row];
                if (!ReferenceEquals(body.Bodies, activities.Bodies)) continue;
                Entity entity = rows.Entities[row];
                ref ActivityInput input = ref MemoryMarshal.Cast<byte, ActivityInput>(activities._inputMapping.Bytes)[body.Index];
                if ((input.Flags & 1) == 0 || input.EntityIndex != (uint)entity.Id || input.EntityGeneration != (uint)entity.Generation) continue;
                foreach (BodyWake wake in rows.ReadBuffer<BodyWake>(row).AsSpan())
                    if (wake.Time >= start && wake.Time < end)
                    {
                        input.Flags |= 8; activities._transientInputs.Add(body.Index); break;
                    }
            }
        }
    }

    public void Dispose()
    {
        _resultMapping.Dispose(); _resultSelectionView.Dispose(); _resultSelection.Dispose();
        _inputMapping.Dispose(); _inputsView.Dispose(); _statesView.Dispose(); States.Dispose(); Inputs.Dispose();
    }
}

public readonly struct DeviceActivityReferenceResources : IJobComponentResources<DeviceBodyActivityBinding>
{
    public static void Collect(in DeviceBodyActivityBinding component, ref JobResourceAccessBuilder resources) { }
}
