using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneContactSupports : IDisposable
{
    private readonly DeviceContactSupports? _supports;
    internal DeviceSceneContactSupports(IGraphicsBackend backend, Device device, World world,
        ReadOnlySpan<Entity> entities, DeviceBodies bodies, DeviceContactManifolds contacts,
        ContactSupportSettings? settings, DeviceGraphCapacity graphCapacity)
    {
        if (settings is not { } supportSettings) return;
        Query rows = world.Query(new QueryDescription().With<ScenePhysicsBody>().With<LongRangeBodySupport>());
        _supports = new(backend, device, world, rows, bodies, contacts, supportSettings, graphCapacity);
        using var structure = world.OpenStructureWrite();
        foreach (Entity body in entities)
        {
            if (world.Read<BodyMass>(body).Motion != BodyMotion.Dynamic) continue;
            world.Add(body, new LongRangeBodySupport());
            world.Add(body, new DeviceSupportBodyBinding(_supports));
        }
        Selections = [_supports];
    }
    internal IDeviceConstraintSelection[] Selections { get; } = [];
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings) => _supports?.AddBindings(bindings);
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => _supports?.GetReadAccesses(scheduler) ?? [];
    internal JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => _supports?.GetWriteAccesses(scheduler) ?? [];
    internal UploadDeviceSceneSupports Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        _supports is null ? dependency : _supports.InitializeBuffers().Schedule(in context, dependency);
    public void Dispose() => _supports?.Dispose();
}
internal readonly struct UploadDeviceSceneSupports(DeviceSceneContactSupports supports) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => supports.Schedule(in context, dependency);
}
