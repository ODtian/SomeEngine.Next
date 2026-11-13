using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal readonly struct RecordPhysicsEvents(Scheduler scheduler, EventReader<PhysicsEvent> reader,
    List<(long Number, PhysicsEvent Event)> values) : IPhysicsStage, IDisposable
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        scheduler.Schedule(new CaptureEvents(scheduler, reader, values),
            [JobResourceAccess.Write(scheduler.GetResourceToken(reader))], dependency);
    private readonly struct CaptureEvents(Scheduler scheduler, EventReader<PhysicsEvent> reader,
        List<(long Number, PhysicsEvent Event)> values) : IJob
    {
        public void Execute()
        {
            EventReadRange range = reader.Capture();
            scheduler.Schedule(new ReadEvents(reader, values, range),
                [range.Read, JobResourceAccess.Write(scheduler.GetResourceToken(values))], range.Ready);
        }
    }
    private readonly struct ReadEvents(EventReader<PhysicsEvent> reader, List<(long Number, PhysicsEvent Event)> values,
        EventReadRange range) : IJob
    {
        public void Execute()
        {
            using EventRead<PhysicsEvent> read = reader.Open(range);
            foreach (PhysicsEvent value in read) values.Add((value.StepNumber, value));
            reader.Advance(read.Through);
        }
    }
    public void Dispose() => reader.Dispose();
}
