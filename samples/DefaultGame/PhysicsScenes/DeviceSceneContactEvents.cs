using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneContactEvents : IDisposable
{
    private readonly DeviceContactManifolds<DeviceAvbdContactState> _contacts;
    private readonly PhysicsEventBuffer _completed;
    private readonly DeviceContactSamples? _samples;
    private readonly DeviceContactResults? _results;
    private readonly DeviceContactEventCounts? _counts;
    private readonly DeviceContactEvents? _events;
    internal DeviceSceneContactEvents(IGraphicsBackend backend, Device device, World world, DeviceBodies bodies,
        DeviceContactManifolds<DeviceAvbdContactState> contacts, DeviceContactResults? results, PhysicsSceneShaders shaders, PhysicsEventBuffer completed,
        bool enabled, bool details)
    {
        _contacts = contacts; _completed = completed;
        if (!enabled) return;
        if (!details) { _counts = new(backend, device, bodies, contacts, shaders.ContactEventCounts(), completed); return; }
        _samples = new(backend, device, bodies, contacts, shaders.ContactSamples());
        _results = results;
        _events = new(world, _samples, _results!.Contacts, completed, .001f);
    }
    internal ObserveDeviceSceneContacts Observe() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_completed.HasReaders) return dependency;
        if (_counts is not null) return _counts.Count().Schedule(in context, dependency);
        if (_samples is null) return dependency;
        return _samples.Readback().Then(_results!.Readback(_contacts)).Then(new ObserveDetails(_events!)).Schedule(in context, dependency);
    }
    public void Dispose() { _events?.Dispose(); _samples?.Dispose(); _counts?.Dispose(); }
    private readonly struct ObserveDetails(DeviceContactEvents events) : IPhysicsStage
    { public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency); }
}
internal readonly struct ObserveDeviceSceneContacts(DeviceSceneContactEvents events) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency);
}
