using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation;

/// <summary>Time and completion counters shared by animation evaluators.</summary>
public struct Timeline : IEnableableComponent
{
    public Timeline() => Speed = 1;

    public float Time;
    public float Delta;
    public float Speed;
    public ushort Stage;
    public TimelineClock Clock;
    public bool Paused;
    public uint Running;
    public uint Completions;
    public uint Starts;

    public void Submit(bool running)
    {
        if (running)
            Running |= 1;
    }

    public void Submit<T>(in MotionSample<T> sample) where T : unmanaged => Submit(!sample.Finished);
}

public enum TimelineClock : byte { Game, Interface, Unscaled }

public enum StageEnd : byte { Duration, Settled, External }

[BufferCapacity(4)]
public struct TimelineStage : IBufferElement
{
    public StageEnd End;
    public float Duration;
}

[BufferCapacity(4)]
public struct TimelineEvent : IBufferElement
{
    public ushort Id;
    public uint Count;
}

/// <summary>Frame time supplied by the application before animation systems run.</summary>
public readonly record struct AnimationTime : IComponent
{
    public AnimationTime(float delta, float gameScale = 1, bool gamePaused = false) =>
        (Delta, GameScale, GamePaused) = (delta, gameScale, gamePaused);

    public float Delta { get; init; }
    public float GameScale { get; init; }
    public bool GamePaused { get; init; }

    public AnimationDeltas Deltas => new(
        GamePaused ? 0 : Delta * GameScale,
        Delta,
        GamePaused ? 0 : Delta);

    public float For(TimelineClock clock) => clock switch
    {
        TimelineClock.Interface => Delta,
        TimelineClock.Unscaled => GamePaused ? 0 : Delta,
        _ => GamePaused ? 0 : Delta * GameScale,
    };
}

/// <summary>Clock selection uses the three deltas computed once at frame start.</summary>
public readonly record struct AnimationDeltas(float Game, float Interface, float Unscaled)
{
    public float For(TimelineClock clock) => clock switch
    {
        TimelineClock.Interface => Interface,
        TimelineClock.Unscaled => Unscaled,
        _ => Game,
    };
}
