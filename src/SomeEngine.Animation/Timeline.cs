using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation;

/// <summary>Time and completion counters shared by animation evaluators.</summary>
public struct Timeline : IEnableableComponent
{
    public Timeline() => Speed = 1;

    public float Time;
    public float Speed;
    public ushort Stage;
    public TimelineClock Clock;
    public uint Running;
    public uint Completions;
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

    public float For(TimelineClock clock) => clock switch
    {
        TimelineClock.Interface => Delta,
        TimelineClock.Unscaled => GamePaused ? 0 : Delta,
        _ => GamePaused ? 0 : Delta * GameScale,
    };
}
