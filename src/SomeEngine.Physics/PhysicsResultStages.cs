using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Source data captured for one step, followed by its independent result consumption.</summary>
public readonly record struct PhysicsResultWork(JobHandle Captured,JobHandle Consumed);

public interface IPhysicsResultCapture
{
    PhysicsResultWork ScheduleResult(in PhysicsStepContext context,JobHandle stepReady,JobHandle precedingConsumption);
}

/// <summary>Captures the selected step before its state is reused; captured outputs remain retained through their consumer.</summary>
public readonly struct PhysicsResultStages<TCapture,TConsumer>(TCapture capture,TConsumer consumer):IPhysicsStage,IPhysicsResultCapture
    where TCapture:struct,IPhysicsStage where TConsumer:struct,IPhysicsStage
{
    public PhysicsResultWork ScheduleResult(in PhysicsStepContext context,JobHandle stepReady,JobHandle precedingConsumption)
    {
        JobHandle captured=capture.Schedule(in context,stepReady);
        JobHandle ready=WorldJobs.GetScheduler(context.World).CombineDependencies([captured,precedingConsumption]);
        return new(captured,consumer.Schedule(in context,ready));
    }
    public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>
        ScheduleResult(in context,dependency,default).Consumed;
}
