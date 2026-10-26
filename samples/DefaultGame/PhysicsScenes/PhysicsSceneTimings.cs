using System.Diagnostics;
using System.Text.Json.Nodes;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>Concrete stage submission costs and the interval between its dependency and complete result.</summary>
public sealed class PhysicsSceneTimings
{
    private readonly List<PhysicsSceneStageTime> _stages = [];
    internal PhysicsSceneStageTime Add(string name, in PhysicsStepContext context)
    {
        var sample = new PhysicsSceneStageTime(name, context.Number, context.Substep);
        _stages.Add(sample);
        return sample;
    }

    public JsonArray Describe() => new(_stages.GroupBy(sample => sample.Name).Select(group => (JsonNode)new JsonObject
    {
        ["Stage"] = group.Key, ["Samples"] = group.Count(),
        ["FirstStep"] = group.Min(sample => sample.Number), ["LastStep"] = group.Max(sample => sample.Number),
        ["MaximumSubstep"] = group.Max(sample => sample.Substep),
        ["Submission"] = Distribution(group.Select(sample => sample.SubmissionMilliseconds)),
        ["DependencyToCompletion"] = Distribution(group.Select(sample => sample.CompletionMilliseconds)),
    }).ToArray());

    internal static JsonObject Distribution(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return new JsonObject
        {
            ["MeanMilliseconds"] = sorted.Average(), ["P95Milliseconds"] = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            ["P99Milliseconds"] = sorted[(int)Math.Ceiling(sorted.Length * .99) - 1], ["MaximumMilliseconds"] = sorted[^1],
        };
    }
}

internal sealed class PhysicsSceneStageTime(string name, long number, int substep)
{
    public string Name { get; } = name;
    public long Number { get; } = number;
    public int Substep { get; } = substep;
    public double SubmissionMilliseconds;
    public long Started, Completed;
    public double CompletionMilliseconds => Stopwatch.GetElapsedTime(Started, Completed).TotalMilliseconds;
}

internal readonly struct MeasuredPhysicsSceneStage<TStage>(TStage stage, PhysicsSceneTimings? timings, string name) : IPhysicsStage
    where TStage : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (timings is null) return stage.Schedule(in context, dependency);
        PhysicsSceneStageTime sample = timings.Add(name, in context);
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        long started = Stopwatch.GetTimestamp();
        JobResourceAccess access = JobResourceAccess.Write(scheduler.GetResourceToken(timings));
        JobHandle ready = scheduler.Schedule(new StartSceneStage(sample), access, dependency);
        JobHandle completion = stage.Schedule(in context, ready);
        JobHandle recorded = scheduler.Schedule(new FinishSceneStage(sample), access, completion);
        sample.SubmissionMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return recorded;
    }

    private readonly struct StartSceneStage(PhysicsSceneStageTime sample) : IJob
    { public void Execute() => sample.Started = Stopwatch.GetTimestamp(); }
    private readonly struct FinishSceneStage(PhysicsSceneStageTime sample) : IJob
    { public void Execute() => sample.Completed = Stopwatch.GetTimestamp(); }
}

internal static class PhysicsSceneTimingStages
{
    public static MeasuredPhysicsSceneStage<TStage> Measure<TStage>(this TStage stage, PhysicsSceneTimings? timings, string name)
        where TStage : struct, IPhysicsStage => new(stage, timings, name);
}
