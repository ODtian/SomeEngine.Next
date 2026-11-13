using SomeEngine.Job;

namespace SomeEngine.Core.ECS;

/// <summary>Physical-result consumption and submission at the game frame's actual read boundaries.</summary>
public interface IGameFramePhysics : IDisposable
{
    JobHandle Completion { get; }
    void AdvanceTime(double deltaTime);
    JobHandle ConsumeCompleted();
    void SubmitDueSteps();
}
