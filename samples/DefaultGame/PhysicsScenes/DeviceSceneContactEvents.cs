using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneContactEvents : IDisposable
{
    private readonly DeviceContactSamples? _samples;
    private readonly DeviceContactEventCounts? _counts;
    private readonly ContactEvents<ScenePhysicsEventStream, DeviceContactEventSource>? _events;

    internal DeviceSceneContactEvents(IGraphicsBackend backend, Device device, World world,
        DeviceBodies bodies, QueryHandle members, QueryHandle points,
        DeviceConstraintData<DeviceContactConstraint> contacts, DeviceConstraintState<DeviceContactResponseState> responses,
        PhysicsSceneShaders shaders, IReadOnlyDictionary<string, ResourceBinding[]> bindings,
        ReadOnlySpan<JobResourceAccess> reads, ReadOnlyMemory<IDeviceConstraintSelection> selections,
        PhysicsEventBuffer completedEvents, bool enabled, int threads, bool details = true)
    {
        if (!enabled) return;
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        if (!details)
        {
            _counts = new(backend, device, bodies, contacts, shaders.ContactEventCounts(), bindings, reads, selections, completedEvents);
            return;
        }
        JobResourceAccess[] accesses = [.. reads, responses.GetReadAccess(scheduler)];
        _samples = new(backend, device, bodies, contacts, shaders.ContactSamples(), bindings,
            accesses, threads, selections);
        _events = new(world, members, points, completedEvents, .001f, new(_samples));
    }

    internal ObserveDeviceSceneContacts Observe() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _samples is null
        ? _counts is null ? dependency : _counts.Count().Schedule(in context, dependency) : _samples.Readback().Then(_events!.Value).Schedule(in context, dependency);

    public void Dispose()
    {
        _events?.Dispose();
        _samples?.Dispose(); _counts?.Dispose();
    }
}

internal readonly struct ObserveDeviceSceneContacts(DeviceSceneContactEvents events) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency);
}
