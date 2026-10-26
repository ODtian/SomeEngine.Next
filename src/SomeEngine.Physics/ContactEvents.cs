using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Marks a point selected for trigger events; response queries select their own contact range.</summary>
public struct TriggerContact : IComponent;

/// <summary>The previous event observation for one selected stream, retained through point destruction until End is recorded.</summary>
public struct ContactEventState<TScope> : ICleanupComponent where TScope : struct
{
    public bool Touching;
    public PhysicsEvent LastEvent;
}

public readonly struct ContactEventComponents<TScope, TComponents>(TComponents components) : IContactPointComponents
    where TScope : struct where TComponents : struct, IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) => components.AddReads(world, accesses);
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        components.Add(ref commands, point, in geometry, first, second, in context);
        commands.Add(point, new ContactEventState<TScope>());
    }
}

/// <summary>Observes selected solved points and their cleanup history in the current step's completion chain.</summary>
public readonly struct ContactEvents<TScope> : IPhysicsStage, IDisposable where TScope : struct
{
    private readonly ContactEventData<TScope> _data;
    public ContactEvents(World world, QueryHandle bodies, QueryHandle points, PhysicsEventBuffer events, float touchDistance) =>
        _data = new(world, bodies, points, events, touchDistance);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.Schedule(0, new Observe(_data, context), _data.Accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        return WorldJobs.GetScheduler(context.World).ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
    }
    private readonly struct Observe(ContactEventData<TScope> data, PhysicsStepContext context) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands) => data.Execute(in context, ref commands);
    }
    public void Dispose() => _data.Dispose();
}

internal sealed class ContactEventData<TScope> : IDisposable where TScope : struct
{
    private readonly World _world;
    private readonly QueryHandle _bodies, _points, _history;
    private readonly PhysicsEventBuffer _events;
    private readonly float _touchDistance;
    private readonly JobResourceToken _resource;
    private HashSet<Entity> _members = [], _current = [];
    internal readonly JobResourceAccess[] Accesses;

    internal ContactEventData(World world, QueryHandle bodies, QueryHandle points, PhysicsEventBuffer events, float touchDistance)
    {
        _world = world; _events = events; _touchDistance = touchDistance;
        _resource = WorldJobs.GetScheduler(world).CreateResourceToken("Contact event membership");
        _bodies = world.Query(world.GetQueryDefinition(bodies).AsReadOnly().WithAccess(new QueryDefinitionBuilder().Read<BodyPose>().Build()));
        QueryDefinition reads = new QueryDefinitionBuilder().Read<ContactPoint>().Read<UndirectedRelationEndpoints<ContactPoint>>()
            .ReadBuffer<ContactBodyLink>().ReadWrite<ContactEventState<TScope>>().Optional<TriggerContact>(QueryAccess.Read)
            .Optional<ContactResponse>(QueryAccess.Read).Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).Build();
        _points = world.Query(world.GetQueryDefinition(points).AsReadOnly().WithAccess(reads));
        _history = world.Query(new QueryDefinitionBuilder().ReadWrite<ContactEventState<TScope>>());
        var accesses = new List<JobResourceAccess> { events.WriteAccess, JobResourceAccess.Write(_resource) };
        foreach (QueryHandle query in new[] { _bodies, _points, _history }) PhysicsQueryAccess.AddAccesses(world, query, accesses);
        Accesses = accesses.ToArray();
    }

    internal void Execute(in PhysicsStepContext context, ref JobCommandWriter commands)
    {
        _current.Clear();
        var read = new ReadBodies(this, context);
        _world.ExecuteQuery(_bodies, ref read);
        var history = new ReadHistory(this, context, commands);
        _world.ExecuteQuery(_history, ref history);
        (_members, _current) = (_current, _members);
    }

    private readonly struct ReadBodies(ContactEventData<TScope> data, PhysicsStepContext context) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            var read = new ReadPoints(data, context, new PhysicsBodies(query));
            data._world.ExecuteQuery(data._points, ref read);
        }
    }
    private readonly ref struct ReadPoints(ContactEventData<TScope> data, PhysicsStepContext context, PhysicsBodies bodies) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                if (!row.Has<ContactEventState<TScope>>()) continue;
                data._current.Add(row.Entity);
                ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
                ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<ContactPoint>>();
                Vector3 delta = ContactBodyMath.GetDelta(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), _bodies, in context, false);
                float separation = point.Separation + Vector3.Dot(point.Normal, delta - point.BodyAnchorDelta);
                bool touching = separation <= data._touchDistance;
                PhysicsEventKind kind = row.Has<TriggerContact>() ? PhysicsEventKind.Trigger : PhysicsEventKind.Contact;
                ref ContactEventState<TScope> state = ref row.ReadWrite<ContactEventState<TScope>>();
                if (state.Touching && (!touching || state.LastEvent.Kind != kind))
                {
                    state.LastEvent.Separation = separation; state.LastEvent.Point = point.WorldPoint; state.LastEvent.Normal = point.Normal;
                    data.End(ref state, in context);
                }
                if (touching)
                {
                    Vector3 force = row.TryReadRef<ContactResponse>(out var response) ? response.Value.ForceOnSecond : default;
                    state.LastEvent = new PhysicsEvent { Kind = kind, Phase = state.Touching ? PhysicsEventPhase.Stay : PhysicsEventPhase.Begin,
                        Subject = row.Entity, First = endpoints.EndpointA, Second = endpoints.EndpointB,
                        Substep = context.Substep, Time = context.EndTime, Point = point.WorldPoint, Normal = point.Normal,
                        Separation = separation, Force = force };
                    data._events.Add(in state.LastEvent);
                }
                state.Touching = touching;
            }
        }
    }
    private ref struct ReadHistory(ContactEventData<TScope> data, PhysicsStepContext context, JobCommandWriter commands) : IQueryExecution
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                if (data._current.Contains(row.Entity)) continue;
                bool destroyed = data._world.IsPendingCleanup(row.Entity);
                if (!destroyed && !data._members.Contains(row.Entity)) continue;
                ref ContactEventState<TScope> state = ref row.ReadWrite<ContactEventState<TScope>>();
                if (state.Touching) data.End(ref state, in context);
                if (destroyed) _commands.Remove<ContactEventState<TScope>>(row.Entity);
            }
        }
    }
    private void End(ref ContactEventState<TScope> state, in PhysicsStepContext context)
    {
        PhysicsEvent value = state.LastEvent;
        value.Phase = PhysicsEventPhase.End; value.Substep = context.Substep; value.Time = context.EndTime;
        value.Force = value.Torque = Vector3.Zero;
        _events.Add(in value); state.Touching = false; state.LastEvent = value;
    }
    public void Dispose()
    {
        foreach (QueryHandle query in new[] { _bodies, _points, _history }) _world.ReleaseQuery(query);
        WorldJobs.GetScheduler(_world).ReleaseResourceToken(_resource);
    }
}
