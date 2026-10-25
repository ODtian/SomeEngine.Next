using SomeEngine.Core.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Step completions and result times for concrete physical work and its game-side consumption.</summary>
public sealed class PhysicsFrame<TStage, TConsumer> : IGameFramePhysics
    where TStage : struct, IPhysicsStage
    where TConsumer : struct, IPhysicsStage
{
    private readonly PhysicsSimulation<TStage> _simulation;
    private readonly TConsumer _consumer;
    private readonly double _stepDuration;
    private readonly double _maximumDelay;
    private readonly long _firstStepNumber;
    private readonly double _startTime;
    private double _elapsedTime;

    public PhysicsFrame(PhysicsSimulation<TStage> simulation, TConsumer consumer, double stepDuration,
        double maximumDelay, long firstStepNumber, double startTime)
    {
        _simulation = simulation;
        _consumer = consumer;
        _stepDuration = stepDuration;
        _maximumDelay = maximumDelay;
        _firstStepNumber = firstStepNumber;
        _startTime = startTime;
        Result = new(firstStepNumber - 1, startTime, startTime, default);
    }

    public PhysicsStepResult Result { get; private set; }
    public PhysicsStepResult? SubmittedStep { get; private set; }
    public JobHandle Completion => SubmittedStep?.Completion ?? Result.Completion;

    public void AdvanceTime(double deltaTime) => _elapsedTime += deltaTime;

    public JobHandle ConsumeCompleted()
    {
        double dueTime = _startTime + DueStepCount * _stepDuration;
        if (dueTime - Result.EndTime >= _maximumDelay)
        {
            SubmitDueSteps();
            return ReadSubmittedStep();
        }
        return SubmittedStep is { } step && step.Completion.IsCompleted ? ReadSubmittedStep() : Result.Completion;
    }

    /// <summary>Completes due physics steps and their consumption at the actual result reader.</summary>
    public PhysicsStepResult CompleteForRead()
    {
        SubmitDueSteps();
        ReadSubmittedStep().Complete();
        return Result;
    }

    public void SubmitDueSteps()
    {
        PhysicsStepResult preceding = SubmittedStep ?? Result;
        long lastNumber = _firstStepNumber - 1 + DueStepCount;
        for (long number = preceding.Number + 1; number <= lastNumber; number++)
        {
            double startTime = _startTime + (number - _firstStepNumber) * _stepDuration;
            preceding = _simulation.Schedule(number, startTime, _stepDuration, preceding.Completion);
            SubmittedStep = preceding;
        }
    }

    private long DueStepCount => (long)Math.Floor(_elapsedTime / _stepDuration);

    private JobHandle ReadSubmittedStep()
    {
        if (SubmittedStep is { } step)
        {
            JobHandle consumed = _simulation.ScheduleConsumption(_consumer, in step);
            Result = new(step.Number, step.StartTime, step.EndTime, consumed);
            SubmittedStep = null;
        }
        return Result.Completion;
    }

    public void Dispose() => Completion.Complete();
}
