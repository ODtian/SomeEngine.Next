using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneContactSupports : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _bodyRows, _contactRows;
    private readonly DeviceContactSupports? _supports;
    private ContactStateInitialization<DeviceContactSupportState>? _inputs;
    private readonly DeviceAvbdStateInputs<LongRangeContactSupport,DeviceContactConstraint,
        DeviceContactSupportState,DeviceContactSupportStateSource>? _edits;
    private readonly DeviceConstraintSelection<DeviceAvbdState<DeviceContactSupportState>, DeviceContactConstraint,
        DeviceAvbdStateRows<DeviceContactSupportState>>? _selected;

    internal DeviceSceneContactSupports(IGraphicsBackend backend, Device device, World world,
        ReadOnlySpan<Entity> entities, DeviceBodies bodies, DeviceConstraintData<DeviceContactConstraint> contacts,
        QueryHandle points, ContactSupportSettings? settings, DeviceGraphCapacity graphCapacity)
    {
        _world = world;
        if (settings is not { } supportSettings) return;
        _bodyRows = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsBody>().Read<LongRangeBodySupport>());
        _contactRows = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>()
            .Read<DeviceAvbdState<DeviceContactSupportState>>());
        _supports = new(backend, device, world, _bodyRows, bodies, contacts, supportSettings, graphCapacity);
        using (var commands = new CommandBuffer(world))
        {
            foreach (Entity body in entities)
            {
                if (world.Read<BodyMass>(body).Motion != BodyMotion.Dynamic) continue;
                commands.Add(body, new LongRangeBodySupport());
                commands.Add(body, new DeviceSupportBodyBinding(_supports));
            }
            commands.Playback();
        }
        _selected = new(backend, device, world, _contactRows, contacts, new(_supports.ContactStates),
            "DeviceContactSupportSelection");
        Selections = [_selected, _supports];
        QueryHandle edited=world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>().Read<LongRangeContactSupport>());
        _edits=new(backend,device,world,edited,contacts,_supports.ContactStates,default);
        world.ReleaseQuery(edited);
    }

    internal IDeviceConstraintSelection[] Selections { get; } = [];
    internal void InitializeContactState(IGraphicsBackend backend,Device device,DeviceContactMatches matches,ComputePipelineDesc kernel)
    {
        if(_supports is not null) _inputs=new(backend,device,matches,_supports.ContactStates,default,kernel);
    }
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings) => _supports?.AddBindings(bindings);
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => _supports?.GetReadAccesses(scheduler) ?? [];
    internal JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => _supports?.GetWriteAccesses(scheduler) ?? [];
    internal void AddContact(ref JobCommandWriter commands, Entity point)
    {
        if (_supports is null) return;
        commands.Add(point, new DeviceAvbdState<DeviceContactSupportState>(_supports.ContactStates));
    }
    internal void AddContact(ref JobCommandWriter commands, DeferredEntity point)
    {
        if (_supports is null) return;
        commands.Add(point, new DeviceAvbdState<DeviceContactSupportState>(_supports.ContactStates));
    }
    internal UploadDeviceSceneSupports Upload(DeviceContactMatches? changes = null) => new(this, changes);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency, DeviceContactMatches? changes) => _supports is null
        ? dependency : _supports.InitializeBuffers().Then(_inputs!.Initialize()).Then(_edits!.Upload()).Schedule(in context, dependency);

    public void Dispose()
    {
        if (_supports is null) return;
        _selected!.Dispose();
        _inputs!.Dispose();
        _edits!.Dispose();
        _supports.Dispose();
        _world.ReleaseQuery(_contactRows);
        _world.ReleaseQuery(_bodyRows);
    }
}

internal readonly struct UploadDeviceSceneSupports(DeviceSceneContactSupports supports, DeviceContactMatches? changes) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => supports.Schedule(in context, dependency, changes);
}
