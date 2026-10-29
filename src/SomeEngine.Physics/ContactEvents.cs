using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
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
    private readonly ContactEvents<TScope, BodyContactEventSource> _events;
    public ContactEvents(World world, QueryHandle bodies, QueryHandle points, PhysicsEventBuffer events, float touchDistance) =>
        _events = new(world, bodies, points, events, touchDistance, default);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _events.Schedule(in context, dependency);
    public void Dispose() => _events.Dispose();
}

/// <summary>Observes one concrete source through the shared point history and cleanup lifecycle.</summary>
public readonly struct ContactEvents<TScope, TSource> : IPhysicsStage, IDisposable
    where TScope : struct where TSource : struct, IContactEventSource
{
    private readonly ContactEventData<TScope, TSource> _data;
    public ContactEvents(World world, QueryHandle bodies, QueryHandle points, PhysicsEventBuffer events, float touchDistance, TSource source) =>
        _data = new(world, bodies, points, events, touchDistance, source);
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.Schedule(0, new Observe(_data, context), _data.Accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle cleanup = scheduler.ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
        return scheduler.CombineDependencies([playback, cleanup]);
    }
    private readonly struct Observe(ContactEventData<TScope, TSource> data, PhysicsStepContext context) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands) => data.Execute(in context, ref commands);
    }
    public void Dispose() => _data.Dispose();
}

internal sealed class ContactEventData<TScope, TSource> : IDisposable
    where TScope : struct where TSource : struct, IContactEventSource
{
    private readonly World _world;
    private readonly QueryHandle _bodies, _points, _history;
    private readonly PhysicsEventBuffer _events;
    private readonly float _touchDistance;
    private readonly JobResourceToken _resource;
    private readonly TSource _source;
    private readonly QueryReadResources _bodyResources, _pointResources;
    private HashSet<Entity> _members = [], _current = [];
    internal readonly JobResourceAccess[] Accesses;

    internal ContactEventData(World world, QueryHandle bodies, QueryHandle points, PhysicsEventBuffer events, float touchDistance, TSource source)
    {
        _world = world; _events = events; _touchDistance = touchDistance; _source = source;
        _bodyResources = new(); _pointResources = new();
        source.AddReadResources(_bodyResources); source.AddReadResources(_pointResources);
        _resource = WorldJobs.GetScheduler(world).CreateResourceToken("Contact event membership");
        _bodies = world.Query(source.AddBodyReads(world.GetQueryDefinition(bodies).AsFilter()).AsReadOnly());
        QueryDefinition reads = new QueryDefinitionBuilder().All<ContactPoint>().ReadWrite<ContactEventState<TScope>>()
            .Optional<TriggerContact>(QueryAccess.Read).Build();
        _points = world.Query(source.AddReads(world.GetQueryDefinition(points).AsFilter()).AsReadOnly().WithAccess(reads));
        _history = world.Query(new QueryDefinitionBuilder().ReadWrite<ContactEventState<TScope>>());
        var accesses = new List<JobResourceAccess> { events.WriteAccess, JobResourceAccess.Write(_resource) };
        PhysicsQueryAccess.AddAccesses(world, _bodies, accesses, _bodyResources);
        PhysicsQueryAccess.AddAccesses(world, _points, accesses, _pointResources);
        PhysicsQueryAccess.AddAccesses(world, _history, accesses);
        source.AddAccesses(WorldJobs.GetScheduler(world), accesses);
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

    private readonly struct ReadBodies(ContactEventData<TScope, TSource> data, PhysicsStepContext context) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            var read = this;
            data._bodyResources.Execute(query, ref read,
                static (scoped QueryCursor rows, ref ReadBodies state) => state.Read(rows));
        }
        private void Read(scoped QueryCursor query)
        {
            var read = new ReadPoints(data, context, new PhysicsBodies(query));
            data._world.ExecuteQuery(data._points, ref read);
        }
    }
    private readonly ref struct ReadPoints(ContactEventData<TScope, TSource> data, PhysicsStepContext context, PhysicsBodies bodies) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        public void Execute(scoped QueryCursor query)
        {
            var read = this;
            data._pointResources.Execute(query, ref read,
                static (scoped QueryCursor rows, ref ReadPoints state) => state.Read(rows));
        }
        private void Read(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                if (!row.Has<ContactEventState<TScope>>()) continue;
                if (!data._source.TryRead(row, _bodies, in context, out ContactEventSample sample)) continue;
                data._current.Add(row.Entity);
                bool touching = sample.Separation <= data._touchDistance;
                PhysicsEventKind kind = row.Has<TriggerContact>() ? PhysicsEventKind.Trigger : PhysicsEventKind.Contact;
                ref ContactEventState<TScope> state = ref row.ReadWrite<ContactEventState<TScope>>();
                if (state.Touching && (!touching || state.LastEvent.Kind != kind))
                {
                    state.LastEvent.Separation = sample.Separation; state.LastEvent.Point = sample.WorldPoint; state.LastEvent.Normal = sample.Normal;
                    data.End(ref state, in context);
                }
                if (touching)
                {
                    state.LastEvent = new PhysicsEvent { Kind = kind, Phase = state.Touching ? PhysicsEventPhase.Stay : PhysicsEventPhase.Begin,
                        Subject = row.Entity, First = sample.First, Second = sample.Second,
                        Substep = context.Substep, Time = context.EndTime, Point = sample.WorldPoint, Normal = sample.Normal,
                        Separation = sample.Separation, Force = sample.Force };
                    data._events.Add(in state.LastEvent);
                }
                state.Touching = touching;
            }
        }
    }
    private ref struct ReadHistory(ContactEventData<TScope, TSource> data, PhysicsStepContext context, JobCommandWriter commands) : IQueryExecution
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
                if (destroyed)
                {
                    _commands.Remove<ContactEventState<TScope>>(row.Entity);
                    if (row.Has<Removed<Outgoing<ContactParticipant>>>())
                        _commands.Remove<Removed<Outgoing<ContactParticipant>>>(row.Entity);
                }
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
