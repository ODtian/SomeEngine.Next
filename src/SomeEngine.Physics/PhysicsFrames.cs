using SomeEngine.Core.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Independent fixed-step work sets, each retaining its own result time and consumption delay.</summary>
public sealed class PhysicsFrames(Scheduler scheduler, params IGameFramePhysics[] frames) : IGameFramePhysics
{
    private readonly JobHandle[] _completions = new JobHandle[frames.Length];

    public JobHandle Completion
    {
        get
        {
            for (int index = 0; index < frames.Length; index++) _completions[index] = frames[index].Completion;
            return scheduler.CombineDependencies(_completions);
        }
    }
    public void AdvanceTime(double deltaTime)
    {
        foreach (IGameFramePhysics frame in frames) frame.AdvanceTime(deltaTime);
    }
    public JobHandle ConsumeCompleted()
    {
        for (int index = 0; index < frames.Length; index++) _completions[index] = frames[index].ConsumeCompleted();
        return scheduler.CombineDependencies(_completions);
    }
    public void SubmitDueSteps()
    {
        foreach (IGameFramePhysics frame in frames) frame.SubmitDueSteps();
    }
    public void Dispose()
    {
        Completion.Complete();
        foreach (IGameFramePhysics frame in frames) frame.Dispose();
    }
}
