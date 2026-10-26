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
public readonly record struct PhysicsStepEvents(long Number, double StartTime, double EndTime) : IComponent;

/// <summary>A historical result; current body and constraint state remains on its original entity.</summary>
[BufferCapacity(8)]
public struct PhysicsEvent : IBufferElement
{
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
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    internal readonly List<PhysicsEvent> Values;
    public PhysicsEventBuffer(Scheduler scheduler, int capacity)
    {
        _scheduler = scheduler; _resource = scheduler.CreateResourceToken("Physics step events"); Values = new(capacity);
    }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_resource);
    public void Add(in PhysicsEvent value) => Values.Add(value);
    public EmitPhysicsEvents<PhysicsEventComponents> Emit() => new(this, default);
    public EmitPhysicsEvents<TComponents> Emit<TComponents>(TComponents components) where TComponents : struct, IPhysicsEventComponents => new(this, components);
    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
}

public readonly struct EmitPhysicsEvents<TComponents>(PhysicsEventBuffer events, TComponents components) : IPhysicsStage
    where TComponents : struct, IPhysicsEventComponents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (context.Substep + 1 < context.Substeps) return dependency;
        var commands = new JobCommandBuffer(context.World, 1);
        commands.ScheduleParallel(new Emit(events, components, context), 1, [events.WriteAccess], dependency);
        JobHandle playback = commands.SchedulePlayback();
        return WorldJobs.GetScheduler(context.World).ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
    }
    private readonly struct Emit(PhysicsEventBuffer events, TComponents components, PhysicsStepContext context) : IJobParallelCommandProducer
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            if (events.Values.Count == 0) return;
            DeferredEntity entity = commands.CreateEntity();
            commands.AddBuffer<PhysicsEvent>(entity, CollectionsMarshal.AsSpan(events.Values));
            components.Add(ref commands, entity);
            commands.Add(entity, new PhysicsStepEvents(context.Number, context.StartTime - context.Substep * context.Duration, context.EndTime));
            events.Values.Clear();
        }
    }
}

public interface IPhysicsEventConsumer
{
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
        _events = world.Query(world.GetQueryDefinition(events).AsReadOnly().WithAccess(
            new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>().Build()));
        var all = new List<JobResourceAccess>(accesses.ToArray());
        PhysicsQueryAccess.AddAccesses(world, _events, all); _accesses = all.ToArray();
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        var commands = new JobCommandBuffer(context.World, 1);
        commands.ScheduleParallel(new Consume(this, context.Number), 1, _accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        return WorldJobs.GetScheduler(context.World).ScheduleFinally(new ReleasePhysicsEventCommands(commands), JobScheduleOptions.Default, playback);
    }
    private readonly struct Consume(ConsumePhysicsEvents<TConsumer> stage, long number) : IJobParallelCommandProducer
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            var read = new Read(stage._consumer, number, commands);
            stage._world.ExecuteReadQuery(stage._events, ref read, static (scoped QueryCursor rows, ref Read state) => state.Execute(rows));
        }
    }
    private ref struct Read(TConsumer consumer, long number, JobCommandWriter commands)
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref readonly PhysicsStepEvents step = ref row.ReadRef<PhysicsStepEvents>();
                if (step.Number > number) continue;
                consumer.Consume(in step, row.ReadBuffer<PhysicsEvent>().AsSpan(), ref _commands);
                _commands.DestroyEntity(row.Entity);
            }
        }
    }
    public void Dispose() => _world.ReleaseQuery(_events);
}

internal readonly record struct ReleasePhysicsEventCommands(JobCommandBuffer Commands) : IJob
{
    public void Execute() => Commands.Dispose();
}
