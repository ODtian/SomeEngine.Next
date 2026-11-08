using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public enum PhysicsEventKind : byte { Contact, Trigger, Break, Sleep, Wake }
public enum PhysicsEventPhase : byte { Begin, Stay, End }

/// <summary>The completed step to which the entity's event buffer belongs.</summary>
public readonly record struct PhysicsStepEvents(long Number, double StartTime, double EndTime) : IEnableableComponent;

/// <summary>The range of completed events belonging to one original physical step.</summary>
[BufferCapacity(8)]
public readonly record struct PhysicsStepEventRange(PhysicsStepEvents Step, int Start, int Count) : IBufferElement;

/// <summary>A historical result; current body and constraint state remains on its original entity.</summary>
[BufferCapacity(8)]
public struct PhysicsEvent : IBufferElement
{
    public PhysicsEvent() { }
    public int Occurrences = 1;
    public PhysicsEventKind Kind;
    public PhysicsEventPhase Phase;
    public Entity Subject;
    /// <summary>The body carrying the reported force/torque for a break result.</summary>
    public Entity Body;
    public Entity First;
    public Entity Second;
    public int Substep;
    public double Time;
    public Vector3 Point;
    public Vector3 Normal;
    public Vector3 Force;
    public Vector3 Torque;
    public float Separation;
}

public interface IPhysicsEventComponents
{
    void Add(ref JobCommandWriter commands, DeferredEntity events);
}

public readonly struct PhysicsEventComponents : IPhysicsEventComponents
{
    public void Add(ref JobCommandWriter commands, DeferredEntity events) { }
}

/// <summary>Reusable substep output, transferred into ordinary ECS buffers at each completed step.</summary>
public sealed class PhysicsEventBuffer : IDisposable
{
    private readonly World _world;
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private Entity _stream;
    private QueryHandle _streamWrites;
    private JobResourceAccess[] _streamAccesses = [];
    internal readonly List<PhysicsEvent> Values;
    public PhysicsEventBuffer(World world, int capacity)
    {
        _world = world; _scheduler = WorldJobs.GetScheduler(world);
        _resource = _scheduler.CreateResourceToken("Physics step events"); Values = new(capacity);
    }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    public void Add(in PhysicsEvent value) => Values.Add(value);
    public EmitPhysicsEvents<PhysicsEventComponents> Emit() => Emit(default(PhysicsEventComponents));
    public EmitPhysicsEvents<TComponents> Emit<TComponents>(TComponents components) where TComponents : struct, IPhysicsEventComponents
    {
        if (_stream == Entity.Null)
        {
            using var commands = new JobCommandBuffer(_world, 1);
            var created = new EventStreamCreation();
            commands.Schedule(0, new CreateStream<TComponents>(created, components));
            commands.SchedulePlayback().Complete();
            _stream = created.Entity.Resolve();
            _world.Disable<PhysicsStepEvents>(_stream);
            _streamWrites = _world.Query(new QueryDefinitionBuilder().ReadWrite<PhysicsStepEvents>()
                .WriteBuffer<PhysicsEvent>().WriteBuffer<PhysicsStepEventRange>());
            var accesses = new List<JobResourceAccess> { WriteAccess };
            PhysicsQueryAccess.AddAccesses(_world, _streamWrites, accesses);
            _streamAccesses = accesses.ToArray();
        }
        return new(this);
    }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        context.Substep + 1 < context.Substeps ? dependency
            : _scheduler.Schedule(new AppendEvents(this, context), _streamAccesses, dependency);

    private sealed class EventStreamCreation { internal DeferredEntity Entity; }
    private readonly struct CreateStream<TComponents>(EventStreamCreation created, TComponents components) : IJobCommandProducer
        where TComponents : struct, IPhysicsEventComponents
    {
        public void Execute(ref JobCommandWriter commands)
        {
            created.Entity = commands.CreateEntity();
            commands.Add(created.Entity, default(PhysicsStepEvents));
            commands.AddBuffer<PhysicsEvent>(created.Entity);
            commands.AddBuffer<PhysicsStepEventRange>(created.Entity);
            components.Add(ref commands, created.Entity);
        }
    }
    private readonly struct AppendEvents(PhysicsEventBuffer events, PhysicsStepContext context) : IJob, IQueryExecution
    {
        public void Execute()
        {
            if (events.Values.Count == 0) return;
            var append = this;
            events._world.ExecuteQuery(events._streamWrites, ref append);
        }
        public void Execute(scoped QueryCursor rows)
        {
            rows.TryGetRow(events._stream, out QueryRow row);
            DynamicBuffer<PhysicsEvent> values = row.Buffer<PhysicsEvent>();
            DynamicBuffer<PhysicsStepEventRange> steps = row.Buffer<PhysicsStepEventRange>();
            var step = new PhysicsStepEvents(context.Number, context.StartTime - context.Substep * context.Duration, context.EndTime);
            if (steps.Count == 0) row.ReadWrite<PhysicsStepEvents>() = step;
            steps.Add(new(step, values.Count, events.Values.Count));
            values.AddRange(CollectionsMarshal.AsSpan(events.Values));
            row.SetComponentEnabled<PhysicsStepEvents>(true);
            events.Values.Clear();
        }
    }
    public void Dispose()
    {
        if (_stream != Entity.Null) { _world.ReleaseQuery(_streamWrites); _world.DestroyEntity(_stream); }
        _scheduler.ReleaseResourceToken(_resource);
    }
}

public readonly struct EmitPhysicsEvents<TComponents>(PhysicsEventBuffer events) : IPhysicsStage
    where TComponents : struct, IPhysicsEventComponents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency);
}

public interface IPhysicsEventConsumer
{
    bool RequiresContactDetails => true;
    void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events, ref JobCommandWriter commands);
}

/// <summary>Consumes selected completed-step entities and acknowledges them in the same command completion chain.</summary>
public readonly struct ConsumePhysicsEvents<TConsumer> : IPhysicsStage, IDisposable where TConsumer : struct, IPhysicsEventConsumer
{
    private readonly World _world;
    private readonly QueryHandle _events;
    private readonly TConsumer _consumer;
    private readonly JobResourceAccess[] _accesses;
    public ConsumePhysicsEvents(World world, QueryHandle events, TConsumer consumer, ReadOnlySpan<JobResourceAccess> accesses = default)
    {
        _world = world; _consumer = consumer;
        _events = world.Query(world.GetQueryDefinition(events).AsFilter().WithAccess(
            new QueryDefinitionBuilder().ReadWrite<PhysicsStepEvents>().WriteBuffer<PhysicsEvent>()
                .WriteBuffer<PhysicsStepEventRange>().Build()));
        var all = new List<JobResourceAccess>(accesses.ToArray());
        PhysicsQueryAccess.AddAccesses(world, _events, all); _accesses = all.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.Schedule(0, new Consume(this, context.Number), _accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle cleanup = scheduler.ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
        return scheduler.CombineDependencies([playback, cleanup]);
    }
    private readonly struct Consume(ConsumePhysicsEvents<TConsumer> stage, long number) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            var read = new Read(stage._consumer, number, commands);
            stage._world.ExecuteQuery(stage._events, ref read);
        }
    }
    private ref struct Read(TConsumer consumer, long number, JobCommandWriter commands) : IQueryExecution
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                DynamicBuffer<PhysicsEvent> values = row.Buffer<PhysicsEvent>();
                DynamicBuffer<PhysicsStepEventRange> steps = row.Buffer<PhysicsStepEventRange>();
                int consumed = 0, eventCount = 0;
                while (consumed < steps.Count)
                {
                    PhysicsStepEventRange range = steps.Read(consumed);
                    if (range.Step.Number > number) break;
                    PhysicsStepEvents step = range.Step;
                    consumer.Consume(in step, values.AsSpan().Slice(range.Start, range.Count), ref _commands);
                    eventCount = range.Start + range.Count;
                    consumed++;
                }
                if (consumed == 0) continue;
                values.RemoveRange(0, eventCount);
                steps.RemoveRange(0, consumed);
                for (int index = 0; index < steps.Count; index++) steps[index] = steps.Read(index) with { Start = steps.Read(index).Start - eventCount };
                if (steps.Count == 0) row.SetComponentEnabled<PhysicsStepEvents>(false);
                else row.ReadWrite<PhysicsStepEvents>() = steps.Read(0).Step;
            }
        }
    }
    public void Dispose() => _world.ReleaseQuery(_events);
}

internal readonly record struct ReleasePhysicsEventCommands(JobCommandBuffer Commands) : IJob
{
    public void Execute() => Commands.Dispose();
}
