using SomeEngine.Job;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidBatchTests
{
    [Fact]
    public void BatchStepsFinishEveryFlowWithTheSameStateAsIndividualSteps()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked });
        LiquidFlow[] individual = [new(scene: "floor"), new(scene: "curved", fluid: LiquidFluid.Urine)];
        LiquidFlow[] together = [new(scene: "floor"), new(scene: "curved", fluid: LiquidFluid.Urine)];
        for (int step = 0; step < 240; step++)
        {
            individual[0].Step();
            individual[1].Step();
            jobs.Schedule(new StepFlowsJob(together)).Complete();
        }

        for (int index = 0; index < individual.Length; index++)
        {
            Assert.Equal(individual[index].Seconds, together[index].Seconds);
            Assert.Equal(individual[index].Volume, together[index].Volume);
            Assert.Equal(individual[index].Momentum, together[index].Momentum);
            Assert.Equal(individual[index].Points.ToArray(), together[index].Points.ToArray());
        }
    }

    private readonly struct StepFlowsJob(LiquidFlow[] flows) : IJob
    {
        public void Execute() => LiquidFlow.Step(flows);
    }
}
