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
    private readonly DeviceAvbdStateInputs<LongRangeContactSupport, DeviceContactConstraint,
        DeviceContactSupportState, DeviceContactSupportStateSource>? _inputs;
    private readonly DeviceConstraintSelection<ContactPoint, DeviceContactConstraint,
        DeviceAvbdStateRows<DeviceContactSupportState>>? _selected;

    internal DeviceSceneContactSupports(IGraphicsBackend backend, Device device, World world,
        ReadOnlySpan<Entity> entities, DeviceBodies bodies, DeviceConstraintData<DeviceContactConstraint> contacts,
        ContactSupportSettings? settings)
    {
        _world = world;
        if (settings is not { } supportSettings) return;
        _bodyRows = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsBody>().Read<LongRangeBodySupport>());
        _contactRows = world.Query(new QueryDefinitionBuilder().All<ScenePhysicsContact>()
            .Read<LongRangeContactSupport>().Read<DeviceConstraintBinding<DeviceContactConstraint>>()
            .Read<DeviceAvbdState<DeviceContactSupportState>>());
        _supports = new(backend, device, world, _bodyRows, bodies, contacts, supportSettings, 32);
        foreach (Entity body in entities)
        {
            if (world.Read<BodyMass>(body).Motion != BodyMotion.Dynamic) continue;
            world.Add(body, new LongRangeBodySupport());
            world.Add(body, new DeviceSupportBodyBinding(_supports));
        }
        _inputs = new(backend, device, world, _contactRows, contacts, _supports.ContactStates, default);
        _selected = new(backend, device, world, _contactRows, contacts, new(_supports.ContactStates),
            "DeviceContactSupportSelection");
        Selections = [_selected, _supports];
    }

    internal IDeviceConstraintSelection[] Selections { get; } = [];
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings) => _supports?.AddBindings(bindings);
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => _supports?.GetReadAccesses(scheduler) ?? [];
    internal JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => _supports?.GetWriteAccesses(scheduler) ?? [];
    internal void AddContact(ref JobCommandWriter commands, Entity point)
    {
        if (_supports is null) return;
        commands.Add(point, new LongRangeContactSupport());
        commands.Add(point, new DeviceAvbdState<DeviceContactSupportState>(_supports.ContactStates));
    }
    internal UploadDeviceSceneSupports Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _supports is null
        ? dependency : _inputs!.Upload().Then(_supports.InitializeBuffers()).Schedule(in context, dependency);

    public void Dispose()
    {
        if (_supports is null) return;
        _selected!.Dispose();
        _inputs!.Dispose();
        _supports.Dispose();
        _world.ReleaseQuery(_contactRows);
        _world.ReleaseQuery(_bodyRows);
    }
}

internal readonly struct UploadDeviceSceneSupports(DeviceSceneContactSupports supports) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => supports.Schedule(in context, dependency);
}
