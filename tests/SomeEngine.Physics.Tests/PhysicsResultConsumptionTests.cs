using System.Collections.Concurrent;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsResultConsumptionTests
{
    [Fact]
    public void CapturedStepsAdvanceWhileAnIndependentConsumerIsBusy()
    {
        using var scheduler=new Scheduler(new(){WorkerCount=2});
        using World world=WorldJobs.CreateWorld(scheduler:scheduler);
        using var started=new ManualResetEventSlim();
        using var release=new ManualResetEventSlim();
        using var advanced=new ManualResetEventSlim();
        var values=new Values(started,release,advanced);
        var simulation=PhysicsSimulation.Create(world,QueryDefinition.Empty,new Advance(values),1);
        using var frame=simulation.CreateFrame(new Capture(values),new Consume(values),1,0,0,0);
        try
        {
            frame.AdvanceTime(1);
            frame.SubmitDueSteps();
            frame.ConsumeCompleted();
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            frame.AdvanceTime(1);
            frame.SubmitDueSteps();
            Assert.True(advanced.Wait(TimeSpan.FromSeconds(5)));
            frame.ConsumeCompleted();
            Assert.Equal(1,frame.Result.Number);
            Assert.Empty(values.Consumed);
        }
        finally
        {
            release.Set();
            frame.Completion.Complete();
        }
        Assert.Equal(new[]{(0L,0),(1L,1)},values.Consumed.ToArray());
    }

    private sealed class Values(ManualResetEventSlim started,ManualResetEventSlim release,ManualResetEventSlim advanced)
    {
        internal int Current;
        internal readonly ConcurrentDictionary<long,int> Captured=new();
        internal readonly ConcurrentQueue<(long,int)> Consumed=new();
        internal readonly ManualResetEventSlim Started=started,Release=release,Advanced=advanced;
    }
    private readonly struct Advance(Values values):IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)
        {
            Scheduler scheduler=WorldJobs.GetScheduler(context.World);
            return scheduler.Schedule(new Write(values,context.Number),[JobResourceAccess.Write(scheduler.GetResourceToken(values))],dependency);
        }
        private readonly struct Write(Values values,long number):IJob
        {
            public void Execute(){values.Current=(int)number;if(number==1)values.Advanced.Set();}
        }
    }
    private readonly struct Capture(Values values):IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)
        {
            Scheduler scheduler=WorldJobs.GetScheduler(context.World);
            return scheduler.Schedule(new Copy(values,context.Number),[JobResourceAccess.Read(scheduler.GetResourceToken(values))],dependency);
        }
        private readonly struct Copy(Values values,long number):IJob
        {
            public void Execute()=>values.Captured[number]=values.Current;
        }
    }
    private readonly struct Consume(Values values):IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>
            WorldJobs.GetScheduler(context.World).Schedule(new Read(values,context.Number),dependency);
        private readonly struct Read(Values values,long number):IJob
        {
            public void Execute()
            {
                if(number==0){values.Started.Set();values.Release.Wait();}
                values.Consumed.Enqueue((number,values.Captured[number]));
            }
        }
    }
}
