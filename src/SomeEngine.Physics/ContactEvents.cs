using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Selects bodies whose shape-pair contacts enter the physical event log.</summary>
public struct ReportContacts : ITag;

/// <summary>Compares completed shape-pair results and records their Begin, Stay and End events.</summary>
public readonly struct ContactEvents : IPhysicsStage, IDisposable
{
    private readonly PhysicsContacts _contacts;
    private readonly PhysicsEventBuffer _events;
    private readonly float _touchDistance;
    private readonly BodyColumns _columns = new();
    private readonly Query _bodies;
    private readonly Scheduler _scheduler;
    private readonly JobResource _history;
    private readonly PairEvents _pairs = new();
    private sealed class PairEvents
    {
        internal Dictionary<PairKey, PairObservation> Previous = [], Current = [];
    }
    public ContactEvents(World world, Query bodies, PhysicsContacts contacts, PhysicsEventBuffer events, float touchDistance)
    {
        _contacts = contacts; _events = events; _touchDistance = touchDistance; _bodies = bodies;
        _scheduler = (world.Execution!.Scheduler!); _history = _scheduler.CreateResource("Completed contact pairs");
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (context.Substep + 1 < context.Substeps) return dependency;
        return PhysicsBodyJobs.Schedule<ReadBodyFields, Observe>(_bodies, _columns, in context, new Observe(this),
            _scheduler.CombineDependencies([dependency, _contacts.Access.Completed]));
    }
    private readonly struct Observe(ContactEvents events) : IPhysicsBodyWork
    {
        public void AddAccesses(World world, List<JobResourceAccess> accesses)
        {
            accesses.Add(events._events.WriteAccess); accesses.Add(JobResourceAccess.Write(events._history));
            foreach (JobResourceAccess read in events._contacts.Access.Reads) accesses.Add(read);
        }
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies) =>
            events.ObservePairs(context.World, bodies, in context);
    }
    private void ObservePairs(World world, scoped PhysicsBodies bodies, in PhysicsStepContext context)
    {
        _pairs.Current.Clear();
        if (!_events.HasReaders) { _pairs.Previous.Clear(); return; }
        ContactView view = _contacts.OpenView();
        for (int pairIndex = 0; pairIndex < view.Pairs.Length; pairIndex++)
        {
            ContactPair pair = view.Pairs[pairIndex];
            PhysicsEvent sample = new()
            {
                First = pair.FirstGeometry,
                Second = pair.SecondGeometry,
                FirstShape = pair.FirstShape,
                SecondShape = pair.SecondShape,
                Substep = context.Substep,
                Time = context.EndTime,
                Separation = float.PositiveInfinity
            };
            bool report = false, trigger = false, touching = false;
            for (int index = pair.FirstPoint; index < pair.FirstPoint + pair.PointCount; index++)
            {
                ContactPoint point = view.GetPoint(index);
                ReadOnlySpan<ContactBodyLink> links = view.BodyLinksOf(index);
                foreach (ContactBodyLink link in links) report |= world.Has<ReportContacts>(link.Body);
                float separation = point.Separation + Vector3.Dot(point.Normal,
                    ContactBodyMath.GetDelta(in point, links, bodies, in context, false, normal: true) - point.BodyAnchorDelta);
                trigger |= (view.Flags[index] & ContactPointFlags.Trigger) != 0;
                if (separation <= _touchDistance)
                { sample.Force += view.Responses[index].ForceOnSecond; touching = true; }
                if (separation >= sample.Separation) continue;
                sample.Separation = separation; sample.Point = point.WorldPoint; sample.Normal = point.Normal;
                sample.FirstFeature = point.FirstFeature; sample.SecondFeature = point.SecondFeature;
            }
            if (!report) continue;
            sample.Kind = trigger ? PhysicsEventKind.Trigger : PhysicsEventKind.Contact;
            var key = new PairKey(pair.FirstGeometry, pair.SecondGeometry, pair.FirstShape, pair.SecondShape, sample.Kind);
            sample.Phase = _pairs.Previous.TryGetValue(key, out PairObservation before) && before.Touching
                ? PhysicsEventPhase.Stay : PhysicsEventPhase.Begin;
            _pairs.Current.Add(key, new(sample, touching));
        }
        foreach ((PairKey key, PairObservation previous) in _pairs.Previous)
            if (previous.Touching && (!_pairs.Current.TryGetValue(key, out PairObservation current) || !current.Touching))
            {
                PhysicsEvent ended = previous.Value;
                if (_pairs.Current.TryGetValue(key, out current))
                {
                    ended.Separation = current.Value.Separation; ended.Point = current.Value.Point; ended.Normal = current.Value.Normal;
                    ended.FirstFeature = current.Value.FirstFeature; ended.SecondFeature = current.Value.SecondFeature;
                }
                ended.Phase = PhysicsEventPhase.End; ended.Force = default;
                ended.Substep = context.Substep; ended.Time = context.EndTime; _events.Add(in ended);
            }
        foreach (PairObservation observation in _pairs.Current.Values)
            if (observation.Touching) { PhysicsEvent value = observation.Value; _events.Add(in value); }
        (_pairs.Previous, _pairs.Current) = (_pairs.Current, _pairs.Previous);
    }
    public void Dispose() { _scheduler.ReleaseResource(_history); _pairs.Previous.Clear(); _pairs.Current.Clear(); }
    private readonly record struct PairObservation(PhysicsEvent Value, bool Touching);
    private readonly record struct PairKey(Entity First, Entity Second, uint FirstShape, uint SecondShape, PhysicsEventKind Kind);
}
