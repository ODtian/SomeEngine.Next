using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public enum PhysicsEventKind : byte { Contact, Trigger, Break, Sleep, Wake }
public enum PhysicsEventPhase : byte { Begin, Stay, End }

/// <summary>A historical physical result, including the complete step interval retained by its event log.</summary>
public struct PhysicsEvent
{
    public PhysicsEvent() { }
    public int Occurrences = 1;
    public PhysicsEventKind Kind;
    public PhysicsEventPhase Phase;
    public Entity Subject;
    public Entity Body;
    public Entity First;
    public Entity Second;
    public uint FirstShape;
    public uint SecondShape;
    public int FirstFeature;
    public int SecondFeature;
    public long StepNumber;
    public double StepStartTime;
    public double StepEndTime;
    public int Substep;
    public double Time;
    public Vector3 Point;
    public Vector3 Normal;
    public Vector3 Force;
    public Vector3 Torque;
    public float Separation;
}

/// <summary>Accumulates substep results and appends them to one event log when the complete step finishes.</summary>
public sealed class PhysicsEventBuffer : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly JobResource _writes;
    private readonly List<PhysicsEvent> _values;
    public PhysicsEventBuffer(World world, int capacity)
    {
        _scheduler = (world.Execution!.Scheduler!);
        _writes = _scheduler.CreateResource("Physics step events"); _values = new(capacity);
        Log = new(_scheduler, capacity);
    }
    public EventLog<PhysicsEvent> Log { get; }
    public bool HasReaders => Log.HasReaders;
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_writes);
    public JobResourceAccess WriteAccess => JobResourceAccess.Write(_writes);
    public EventReader<PhysicsEvent> Watch(Guid id = default) => id == default ? Log.Watch() : Log.Watch(id);
    public void Add(in PhysicsEvent value) { if (Log.HasReaders) _values.Add(value); }
    public EmitPhysicsEvents Emit() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        context.Substep + 1 < context.Substeps ? dependency
            : _scheduler.Schedule(new Append(this, context), [WriteAccess, Log.Write], dependency: dependency);
    private readonly struct Append(PhysicsEventBuffer events, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            if (events._values.Count == 0) return;
            using EventWriter<PhysicsEvent> writer = events.Log.Reserve(events._values.Count);
            Span<PhysicsEvent> destination = writer.Values;
            if (!destination.IsEmpty)
            {
                CollectionsMarshal.AsSpan(events._values).CopyTo(destination);
                for (int index = 0; index < destination.Length; index++)
                {
                    ref PhysicsEvent value = ref destination[index];
                    value.StepNumber = context.Number;
                    value.StepStartTime = context.StartTime - context.Substep * context.Duration;
                    value.StepEndTime = context.EndTime;
                }
            }
            events._values.Clear();
            events.Log.Publish(writer.Range);
        }
    }
    public void Dispose() { Log.Dispose(); _scheduler.ReleaseResource(_writes); _values.Clear(); }
}

public readonly struct EmitPhysicsEvents(PhysicsEventBuffer events) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency);
}
