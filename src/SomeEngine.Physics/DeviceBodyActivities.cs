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
public sealed class DeviceBodyActivities : IDisposable
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
    private MappedBuffer _inputMapping;

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
        QueryDefinition definition = world.GetQueryDefinition(members).AsFilter().WithAccess(new QueryDefinitionBuilder()
            .Read<DeviceBodyBinding>().Optional<DeviceBodyActivityBinding>(QueryAccess.Read)
            .Optional<BodyActivity>(QueryAccess.Read).OptionalBuffer<BodyWake>(QueryAccess.Read).Build());
        SourceRows = world.Query(definition);
        ResultRows = world.Query(definition.WithAccess(new QueryDefinitionBuilder().ReadWrite<BodyActivity>().Build()));
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
        _inputMapping.Bytes.Clear();
        var read = new ReadSources(this, context.StartTime, context.EndTime);
        World.ExecuteReadQuery(SourceRows, ref read,
            static (scoped QueryCursor rows, ref ReadSources read) => read.Execute(rows));
        _inputMapping.Flush(_inputMapping.Range);
    }

    private readonly record struct CpuVersion(Entity Entity, uint Activity);
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ActivityInput
    {
        public uint EntityIndex, EntityGeneration, Flags, Sleeping;
        public float QuietSeconds, Radius;
    }
    private readonly record struct ReadSources(DeviceBodyActivities Activities, double Start, double End)
    {
        internal void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                var binding = row.Read<DeviceBodyBinding>();
                if (!ReferenceEquals(binding.Bodies, Activities.Bodies)) continue;
                bool hasActivity = row.TryRead<DeviceBodyActivityBinding>(out var activityBinding)
                    && ReferenceEquals(activityBinding.Activities, Activities) && row.Has<BodyActivity>();
                uint version = hasActivity ? row.WriteVersion<BodyActivity>() : 0;
                var current = new CpuVersion(row.Entity, version);
                var input = new ActivityInput { EntityIndex = (uint)row.Entity.Index,
                    EntityGeneration = (uint)row.Entity.Generation, Flags = hasActivity ? 3u : 1u };
                if (current != Activities._versions[binding.Index]) input.Flags |= 4;
                if (hasActivity)
                {
                    ref readonly BodyActivity activity = ref row.ReadRef<BodyActivity>();
                    input.Sleeping = activity.Sleeping ? 1u : 0u;
                    input.QuietSeconds = activity.QuietSeconds; input.Radius = activity.Radius;
                }
                if (row.HasBuffer<BodyWake>())
                    foreach (BodyWake wake in row.ReadBuffer<BodyWake>().AsSpan())
                        if (wake.Time >= Start && wake.Time < End) { input.Flags |= 8; break; }
                MemoryMarshal.Write(Activities._inputMapping.Bytes.Slice(binding.Index * 32, 32), in input);
                Activities._versions[binding.Index] = current;
            }
        }
    }

    internal void ApplyResults(in PhysicsStepContext context, PhysicsEventBuffer events)
    {
        var apply = new Apply(this, context, events);
        World.ExecuteQuery(ResultRows, ref apply);
    }
    private readonly ref struct Apply(DeviceBodyActivities activities, PhysicsStepContext context, PhysicsEventBuffer events) : IQueryExecution
    {
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                var body = row.Read<DeviceBodyBinding>();
                if (!ReferenceEquals(body.Bodies, activities.Bodies) || !row.TryRead<DeviceBodyActivityBinding>(out var binding)
                    || !ReferenceEquals(binding.Activities, activities)) continue;
                ref readonly DeviceBodyActivityState result = ref activities.Results[body.Index];
                if (result.EntityIndex != (uint)row.Entity.Index || result.EntityGeneration != (uint)row.Entity.Generation) continue;
                CpuVersion version = activities._versions[body.Index];
                if (version.Entity == row.Entity && version.Activity == row.WriteVersion<BodyActivity>())
                {
                    ref BodyActivity activity = ref row.ReadWrite<BodyActivity>();
                    activity.Sleeping = (result.Flags & 4) != 0; activity.QuietSeconds = result.QuietSeconds;
                    activity.Radius = result.Radius; activity.MotionPredicted = (result.Flags & 8) != 0;
                    activities._versions[body.Index] = new(row.Entity, row.WriteVersion<BodyActivity>());
                }
                if ((result.Flags & 16) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Wake,
                    Subject = row.Entity, Substep = context.Substep,
                    Time = (result.Flags & 64) != 0 ? context.EndTime : context.StartTime });
                if ((result.Flags & 32) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Sleep,
                    Subject = row.Entity, Substep = context.Substep, Time = context.EndTime });
            }
        }
    }
    public void Dispose()
    {
        World.ReleaseQuery(SourceRows); World.ReleaseQuery(ResultRows);
        _inputMapping.Dispose(); _inputsView.Dispose(); _statesView.Dispose(); States.Dispose(); Inputs.Dispose();
    }
}

public readonly struct DeviceActivityReferenceResources : IJobComponentResources<DeviceBodyActivityBinding>
{
    public static void Collect(in DeviceBodyActivityBinding component, ref JobResourceAccessBuilder resources) { }
}
