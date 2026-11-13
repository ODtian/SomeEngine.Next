using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

public interface IScenePhysicsEvents
{
    bool ReadsEvents => true;
    bool RequiresContactDetails => true;
    void Consume(ReadOnlySpan<PhysicsEvent> events);
}

internal readonly struct AcknowledgeSceneEvents : IScenePhysicsEvents
{
    public bool ReadsEvents => false;
    public void Consume(ReadOnlySpan<PhysicsEvent> events) { }
}

internal sealed class ConsumeSceneEvents<TEvents> : IDisposable where TEvents : struct, IScenePhysicsEvents
{
    private readonly Scheduler _scheduler;
    private readonly EventReader<PhysicsEvent>? _reader;
    private readonly TEvents _consumer;
    private readonly JobResourceAccess[] _accesses;
    internal ConsumeSceneEvents(Scheduler scheduler, PhysicsEventBuffer events, TEvents consumer,
        ReadOnlySpan<JobResourceAccess> accesses)
        => (_scheduler, _reader, _consumer, _accesses) = (scheduler, consumer.ReadsEvents ? events.Watch() : null, consumer, accesses.ToArray());
    internal ReadSceneEvents<TEvents> Read() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        _reader is null ? dependency : _scheduler.Schedule(new Capture(this),
            [JobResourceAccess.Write(_scheduler.GetResourceToken(this))], dependency);
    private readonly struct Capture(ConsumeSceneEvents<TEvents> events) : IJob
    {
        public void Execute()
        {
            EventReadRange range = events._reader!.Capture();
            JobResourceAccess[] accesses = [range.Read, .. events._accesses,
                JobResourceAccess.Write(events._scheduler.GetResourceToken(events))];
            events._scheduler.Schedule(new ReadEvents(events, range), accesses, range.Ready);
        }
    }
    private readonly struct ReadEvents(ConsumeSceneEvents<TEvents> events, EventReadRange range) : IJob
    {
        public void Execute()
        {
            using EventRead<PhysicsEvent> read = events._reader!.Open(range);
            TEvents consumer = events._consumer;
            consumer.Consume(read.First); consumer.Consume(read.Second);
            events._reader.Advance(read.Through);
        }
    }
    public void Dispose() => _reader?.Dispose();
}
internal readonly struct ReadSceneEvents<TEvents>(ConsumeSceneEvents<TEvents> events) : IPhysicsStage
    where TEvents : struct, IScenePhysicsEvents
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => events.Schedule(in context, dependency);
}
