using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Selects the device representation of this body's original activity component.</summary>
public readonly record struct DeviceBodyActivityBinding(DeviceBodyActivities Activities) : IComponent;

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
    internal readonly IGraphicsBackend Backend;
    internal readonly Device Device;
    internal readonly DeviceBodies Bodies;
    internal readonly World World;
    internal readonly Buffer States, Inputs;
    internal readonly QueryHandle SourceRows, ResultRows;
    internal readonly JobResourceAccess[] SourceAccesses, ResultAccesses;
    internal readonly DeviceBodyActivityState[] Results;
    private readonly BufferUav _statesView;
    private readonly BufferSrv _inputsView;
    private readonly CpuVersion[] _versions;
    private readonly QueryHandle _wakeRows, _activityMembers;
    private readonly List<int> _transientInputs = [];
    private MappedBuffer _inputMapping;
    private uint _membershipVersion, _activityMembershipVersion, _lastSystemVersion;

    public DeviceBodyActivities(IGraphicsBackend backend, Device device, World world, QueryHandle members, DeviceBodies bodies)
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
        _inputMapping = backend.Map(Inputs, MapType.Write, BufferRange.Whole);
        _inputMapping.Bytes.Clear();
        QueryDefinition definition = world.GetQueryDefinition(members).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<DeviceBodyBinding>().Optional<DeviceBodyActivityBinding>(QueryAccess.Read)
            .Optional<BodyActivity>(QueryAccess.Read).OptionalBuffer<BodyWake>(QueryAccess.Read).Build());
        SourceRows = world.Query(definition);
        ResultRows = world.Query(definition.WithAccess(new QueryDefinitionBuilder().ReadWrite<BodyActivity>().Build()));
        _resultReads = world.Query(world.GetQueryDefinition(ResultRows).AsReadOnly());
        _wakeRows = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().ReadBuffer<BodyWake>());
        _activityMembers = world.Query(new QueryDefinitionBuilder().All<DeviceBodyBinding>()
            .All<DeviceBodyActivityBinding>().All<BodyActivity>());
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceBodyActivityBinding, DeviceActivityReferenceResources>();
        SourceAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, SourceAccesses, resources);
        ResultAccesses = new JobResourceAccess[QueryJobAccess.Count(world.GetQueryDefinition(ResultRows))];
        QueryJobAccess.CopyAccesses(world, world.GetQueryDefinition(ResultRows), ResultAccesses, resources);
    }

    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("DeviceBodyActivities", [ResourceBinding.WritableBuffer(_statesView)]);
        bindings.Add("DeviceBodyActivityInputs", [ResourceBinding.ReadOnlyBuffer(_inputsView)]);
        Bodies.AddCpuInputBindings(bindings);
    }
    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(States), 0, (long)States.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size),
            .. Bodies.GetCpuInputReadAccesses(scheduler)];

    public JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Write(scheduler.GetResourceToken(States), 0, (long)States.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(Inputs), 0, (long)Inputs.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)), .. Bodies.GetCpuInputReadAccesses(scheduler)];

    internal void CaptureInputs(in PhysicsStepContext context)
    {
        uint membership = World.MembershipVersion(SourceRows);
        uint activityMembership = World.MembershipVersion(_activityMembers);
        bool readAll = membership != _membershipVersion || activityMembership != _activityMembershipVersion;
        if (readAll) _inputMapping.Bytes.Clear();
        else
        {
            Span<ActivityInput> inputs = MemoryMarshal.Cast<byte, ActivityInput>(_inputMapping.Bytes);
            foreach (int index in _transientInputs) inputs[index].Flags &= 3;
        }
        _transientInputs.Clear();
        var source = new ReadSources(this, readAll);
        var read = (Source: source, Version: 0u);
        World.ExecuteReadQuery(SourceRows, _lastSystemVersion, ref read,
            static (scoped QueryCursor rows, ref (ReadSources Source, uint Version) read) =>
            {
                read.Source.Execute(rows);
                read.Version = rows.CurrentSystemVersion;
            });
        var wakes = new ReadWakes(this, context.StartTime, context.EndTime);
        World.ExecuteReadQuery(_wakeRows, ref wakes,
            static (scoped QueryCursor rows, ref ReadWakes wakes) => wakes.Execute(rows));
        _inputMapping.Flush(_inputMapping.Range);
        _membershipVersion = membership;
        _activityMembershipVersion = activityMembership;
        _lastSystemVersion = read.Version;
    }

    private readonly record struct CpuVersion(Entity Entity, uint Activity);
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ActivityInput
    {
        public uint EntityIndex, EntityGeneration, Flags, Sleeping;
        public float QuietSeconds, Radius;
    }
    private readonly record struct ReadSources(DeviceBodyActivities Activities, bool ReadAll)
    {
        internal void Execute(scoped QueryCursor rows)
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                bool hasActivity = chunk.Has<BodyActivity>();
                bool hasBinding = chunk.Has<DeviceBodyActivityBinding>();
                bool bindingsChanged = ReadAll || chunk.HasChangedSinceLastSystemVersion<DeviceBodyBinding>() ||
                    hasBinding && chunk.HasChangedSinceLastSystemVersion<DeviceBodyActivityBinding>();
                if (!bindingsChanged && (!hasActivity || !chunk.HasChangedSinceLastSystemVersion<BodyActivity>())) continue;
                ReadOnlySpan<uint> versions = hasActivity ? chunk.ReadWriteVersions<BodyActivity>() : default;
                foreach (int index in chunk.RowIndices)
                {
                    DeviceBodyBinding body = chunk.Read<DeviceBodyBinding>(index);
                    if (!ReferenceEquals(body.Bodies, Activities.Bodies)) continue;
                    Entity entity = chunk.GetEntity(index);
                    uint version = hasActivity ? versions[index] : 0;
                    if (!bindingsChanged && new CpuVersion(entity, version) == Activities._versions[body.Index]) continue;
                    bool selected = hasActivity && hasBinding &&
                        ReferenceEquals(chunk.Read<DeviceBodyActivityBinding>(index).Activities, Activities);
                    BodyActivity activity = selected ? chunk.Read<BodyActivity>(index) : default;
                    Read(entity, in body, selected, in activity, selected ? version : 0);
                }
            }
        }
        private void Read(Entity entity, in DeviceBodyBinding binding, bool hasActivity, in BodyActivity activity, uint version)
        {
            var current = new CpuVersion(entity, version);
            if (!ReadAll && current == Activities._versions[binding.Index]) return;
            var input = new ActivityInput { EntityIndex = (uint)entity.Index,
                EntityGeneration = (uint)entity.Generation, Flags = hasActivity ? 3u : 1u };
            if (current != Activities._versions[binding.Index]) input.Flags |= 4;
            if (hasActivity)
            {
                input.Sleeping = activity.Sleeping ? 1u : 0u;
                input.QuietSeconds = activity.QuietSeconds; input.Radius = activity.Radius;
            }
            MemoryMarshal.Write(Activities._inputMapping.Bytes.Slice(binding.Index * 32, 32), in input);
            if ((input.Flags & 4) != 0) Activities._transientInputs.Add(binding.Index);
            Activities._versions[binding.Index] = current;
        }
    }
    private readonly record struct ReadWakes(DeviceBodyActivities Activities, double Start, double End)
    {
        internal void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                DeviceBodyBinding body = row.Read<DeviceBodyBinding>();
                if (!ReferenceEquals(body.Bodies, Activities.Bodies)) continue;
                ref ActivityInput input = ref MemoryMarshal.Cast<byte, ActivityInput>(Activities._inputMapping.Bytes)[body.Index];
                if ((input.Flags & 1) == 0 || input.EntityIndex != (uint)row.Entity.Index
                    || input.EntityGeneration != (uint)row.Entity.Generation) continue;
                foreach (BodyWake wake in row.ReadBuffer<BodyWake>().AsSpan())
                    if (wake.Time >= Start && wake.Time < End)
                    {
                        input.Flags |= 8;
                        Activities._transientInputs.Add(body.Index);
                        break;
                    }
            }
        }
    }

    public void Dispose()
    {
        World.ReleaseQuery(SourceRows); World.ReleaseQuery(ResultRows); World.ReleaseQuery(_resultReads); World.ReleaseQuery(_wakeRows);
        World.ReleaseQuery(_activityMembers);
        _inputMapping.Dispose(); _inputsView.Dispose(); _statesView.Dispose(); States.Dispose(); Inputs.Dispose();
    }
}

public readonly struct DeviceActivityReferenceResources : IJobComponentResources<DeviceBodyActivityBinding>
{
    public static void Collect(in DeviceBodyActivityBinding component, ref JobResourceAccessBuilder resources) { }
}
