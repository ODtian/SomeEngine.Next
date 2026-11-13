using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Emits detailed shape-pair transitions from explicitly completed device observations.</summary>
public sealed class DeviceContactEvents : IPhysicsStage, IDisposable
{
    private readonly World _world;
    private readonly DeviceContactSamples _samples;
    private readonly PhysicsContacts _contacts;
    private readonly PhysicsEventBuffer _events;
    private readonly float _touchDistance;
    private readonly Scheduler _scheduler;
    private readonly JobResource _history;
    private Dictionary<PairKey, PhysicsEvent> _previous = [], _current = [];
    public DeviceContactEvents(World world, DeviceContactSamples samples, PhysicsContacts contacts, PhysicsEventBuffer events, float touchDistance)
    {
        _world = world; _samples = samples; _contacts = contacts; _events = events; _touchDistance = touchDistance;
        _scheduler = WorldJobs.GetScheduler(world); _history = _scheduler.CreateResource("Device contact event pairs");
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobResourceAccess[] accesses = [..((WorldJobs)_world.Execution!).StructureRead,_samples.ReadAccess(_scheduler),
            .._contacts.Access.Reads,_events.WriteAccess,JobResourceAccess.Write(_history)];
        return _scheduler.Schedule(new Observe(this, context), accesses,
            _scheduler.CombineDependencies([dependency, _contacts.Access.Completed]));
    }
    private void ObservePairs(in PhysicsStepContext context)
    {
        _current.Clear();
        if (!_events.HasReaders) { _previous.Clear(); return; }
        ContactView view = _contacts.OpenView();
        ReadOnlySpan<DeviceContactSample> values = _samples.Values;
        for (int point = 0; point < values.Length; point++)
        {
            ref readonly DeviceContactSample sample = ref values[point];
            if (sample.Active == 0) continue;
            bool report = false;
            foreach (ContactBodyLink link in view.BodyLinksOf(point)) report |= _world.Has<ReportContacts>(link.Body);
            if (!report) continue;
            ContactPair pair = sample.Pair;
            PhysicsEventKind kind = (sample.Flags & ContactPointFlags.Trigger) != 0 ? PhysicsEventKind.Trigger : PhysicsEventKind.Contact;
            var key = new PairKey(pair.FirstGeometry, pair.SecondGeometry, pair.FirstShape, pair.SecondShape, kind);
            if (!_current.TryGetValue(key, out PhysicsEvent value))
                value = new()
                {
                    First = pair.FirstGeometry,
                    Second = pair.SecondGeometry,
                    FirstShape = pair.FirstShape,
                    SecondShape = pair.SecondShape,
                    Kind = kind,
                    Occurrences = 0,
                    Separation = float.PositiveInfinity,
                    Phase = _previous.TryGetValue(key, out PhysicsEvent previous) && previous.Occurrences != 0 ? PhysicsEventPhase.Stay : PhysicsEventPhase.Begin,
                    Substep = context.Substep,
                    Time = context.EndTime
                };
            if (sample.Separation <= _touchDistance) { value.Occurrences++; value.Force += sample.Force; }
            if (sample.Separation < value.Separation)
            {
                value.Separation = sample.Separation; value.Point = sample.WorldPoint; value.Normal = sample.Normal;
                value.FirstFeature = sample.FirstFeature; value.SecondFeature = sample.SecondFeature;
            }
            _current[key] = value;
        }
        foreach ((PairKey key, PhysicsEvent previous) in _previous)
            if (previous.Occurrences != 0 && (!_current.TryGetValue(key, out PhysicsEvent observed) || observed.Occurrences == 0))
            {
                PhysicsEvent value = _current.TryGetValue(key, out observed) ? observed : previous;
                value.Phase = PhysicsEventPhase.End; value.Force = default; value.Occurrences = previous.Occurrences;
                value.Substep = context.Substep; value.Time = context.EndTime; _events.Add(in value);
            }
        foreach (PhysicsEvent value in _current.Values) if (value.Occurrences != 0) _events.Add(in value);
        (_current, _previous) = (_previous, _current);
    }
    private readonly struct Observe(DeviceContactEvents events, PhysicsStepContext context) : IJob
    { public void Execute() => events.ObservePairs(in context); }
    private readonly record struct PairKey(Entity First, Entity Second, uint FirstShape, uint SecondShape, PhysicsEventKind Kind);
    public void Dispose() { _scheduler.ReleaseResource(_history); _current.Clear(); _previous.Clear(); }
}
