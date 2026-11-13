using SomeEngine.Core.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IPhysicsFrame : IGameFramePhysics
{
    PhysicsStepResult Result { get; }
    PhysicsStepResult CompleteForRead();
}

/// <summary>Step completions and result times for physics work and its game-side consumption.</summary>
public sealed class PhysicsFrame<TStage, TConsumer> : IPhysicsFrame
    where TStage : struct, IPhysicsStage
    where TConsumer : struct, IPhysicsStage
{
    private readonly PhysicsSimulation<TStage> _simulation;
    private readonly TConsumer _consumer;
    private readonly double _stepDuration;
    private readonly double _maximumDelay;
    private readonly long _firstStepNumber;
    private readonly double _startTime;
    private double _elapsedSteps;
    private PhysicsStepResult _simulationTail;
    private JobHandle _consumptionTail;
    private readonly IPhysicsResultCapture? _capture;

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
        _simulationTail = Result;
        _capture = consumer is IPhysicsResultCapture capture ? capture : null;
    }

    public PhysicsStepResult Result { get; private set; }
    public PhysicsStepResult? SubmittedStep { get; private set; }
    public JobHandle Completion { get; private set; }

    public void AdvanceTime(double deltaTime) => _elapsedSteps += deltaTime / _stepDuration;

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
        PhysicsStepResult preceding = _simulationTail;
        long lastNumber = _firstStepNumber - 1 + DueStepCount;
        for (long number = preceding.Number + 1; number <= lastNumber; number++)
        {
            double startTime = _startTime + (number - _firstStepNumber) * _stepDuration;
            preceding = _simulation.Schedule(number, startTime, _stepDuration, preceding.Completion);
            SubmittedStep = preceding;
            _simulationTail = preceding;
            Completion = _simulation.Scheduler.CombineDependencies([preceding.Completion, _consumptionTail]);
        }
    }

    private long DueStepCount => (long)Math.Floor(_elapsedSteps);

    private JobHandle ReadSubmittedStep()
    {
        if (SubmittedStep is { } step)
        {
            JobHandle captured, consumed;
            if (_capture is not null)
            {
                PhysicsStepContext context = _simulation.ResultContext(in step);
                PhysicsResultWork result = _capture.ScheduleResult(in context, step.Completion, _consumptionTail);
                captured = result.Captured;
                consumed = result.Consumed;
            }
            else
            {
                consumed = _simulation.ScheduleConsumption(_consumer, in step, _consumptionTail);
                captured = consumed;
            }
            _simulationTail = _simulationTail with
            {
                Completion = _simulation.Scheduler.CombineDependencies([_simulationTail.Completion, captured]),
            };
            _consumptionTail = consumed;
            Result = new(step.Number, step.StartTime, step.EndTime, consumed);
            SubmittedStep = null;
            Completion = _simulation.Scheduler.CombineDependencies([_simulationTail.Completion, _consumptionTail]);
        }
        return Result.Completion;
    }

    public void Dispose() => Completion.Complete();
}
