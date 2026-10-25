using System.Diagnostics;
using SomeEngine.Animation;
using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Runtime;

public enum FrameStage : byte
{
    PlatformInput,
    InterfaceInput,
    GameInputAndSimulation,
    ViewRebuild,
    InterfaceUpdate,
    Animation,
    TextMeasurement,
    Layout,
    Flip,
    Extraction,
}

/// <summary>A frame stage and its registered systems; application boundaries have an empty system list.</summary>
public readonly record struct FrameStageDescription(FrameStage Stage, IReadOnlyList<Type> SystemTypes);

/// <summary>CPU stage times and current-thread allocations for one measured view frame.</summary>
public readonly record struct ViewTiming(int RebuildCount, int ComputationCount, double RebuildMs,
    double TextMs, double LayoutMs, double TotalMs, long AllocatedBytes, double AnimationMs = 0);

/// <summary>Game-world interface systems, with input before simulation and layout before extraction.</summary>
public sealed class GameFrameSystems : IDisposable
{
    private readonly World _world;
    private readonly (FrameStage Stage, SystemGroup<ImmediateSystemContext>? Systems)[] _stages;
    private readonly ViewRebuildSystem _rebuild = new();
    private static readonly TraceSite RebuildTrace = new("someui", "Views.Rebuild");
    private static readonly TraceSite TextTrace = new("someui", "Views.TextMeasurement");
    private static readonly TraceSite LayoutTrace = new("someui", "Views.Layout");
    private static readonly TraceSite AnimationTrace = new("someui", "Views.Animation");

    public GameFrameSystems(World world, bool includeInterface = true)
    {
        _world = world;
        _stages = Enum.GetValues<FrameStage>().Select(stage => (stage,
            stage is FrameStage.PlatformInput or FrameStage.Extraction
                ? null : new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world)))).ToArray();
        AnimationSystems.AddDefaults(Group(FrameStage.Animation));
        if (includeInterface)
        {
            Group(FrameStage.InterfaceInput).Add(new HitTestSystem());
            Group(FrameStage.InterfaceInput).Add(new FocusNavigationSystem());
            Group(FrameStage.ViewRebuild).Add(_rebuild);
            var shaping = new TextShapingSystem();
            Group(FrameStage.TextMeasurement).Add(shaping);
            Group(FrameStage.Layout).Add(new LayoutSystem(shaping));
            Group(FrameStage.Flip).Add(new FlipSystem());
        }
    }

    /// <summary>Describes actual registration and the surrounding application boundaries.</summary>
    public FrameStageDescription[] Describe() => _stages.Select(stage =>
        new FrameStageDescription(stage.Stage, stage.Systems is null ? [] : stage.Stage == FrameStage.Animation
            ? AnimationSystems.Describe(stage.Systems) : stage.Systems.DescribeSystems())).ToArray();

    /// <summary>Consumes platform input using the previous layout, before game input runs.</summary>
    public void UpdateInput() => Group(FrameStage.InterfaceInput).Update();

    /// <summary>Runs after simulation; the application extracts the completed result afterwards.</summary>
    public void UpdateViews(double deltaSeconds) => UpdateViewsCore(deltaSeconds, measure: false);

    /// <summary>Runs the same view stages and returns measurement data for the completed frame.</summary>
    public ViewTiming MeasureViews(double deltaSeconds) => UpdateViewsCore(deltaSeconds, measure: true);

    private ViewTiming UpdateViewsCore(double deltaSeconds, bool measure)
    {
        ViewTiming result = default;
        foreach (ValueTask completion in UpdateViewsSteps(deltaSeconds, measure, value => result = value))
        {
            if (completion.IsCompletedSuccessfully) completion.GetAwaiter().GetResult();
            else completion.AsTask().GetAwaiter().GetResult();
        }
        return result;
    }

    internal IEnumerable<ValueTask> UpdateViewsSteps(double deltaSeconds, bool measure, Action<ViewTiming> completed)
    {
        long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        double rebuildMs = 0, textMs = 0, layoutMs = 0, animationMs = 0;
        var context = new ImmediateSystemContext(_world, 0, 0, deltaSeconds);
        foreach (var stage in _stages)
        {
            if (stage.Stage < FrameStage.GameInputAndSimulation || stage.Systems is null) continue;
            long stageStarted = measure ? Stopwatch.GetTimestamp() : 0;
            TraceSite? site = stage.Stage switch
            {
                FrameStage.ViewRebuild => RebuildTrace,
                FrameStage.Animation => AnimationTrace,
                FrameStage.TextMeasurement => TextTrace,
                FrameStage.Layout => LayoutTrace,
                _ => null,
            };
            using (site is null ? default : EngineTrace.Begin(site)) stage.Systems.Update(ref context);
            yield return stage.Systems.CompleteAsync();
            if (!measure) continue;
            double elapsed = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            switch (stage.Stage)
            {
                case FrameStage.ViewRebuild: rebuildMs = elapsed; break;
                case FrameStage.Animation: animationMs = elapsed; break;
                case FrameStage.TextMeasurement: textMs = elapsed; break;
                case FrameStage.Layout: layoutMs = elapsed; break;
            }
        }
        completed(measure ? new ViewTiming(_rebuild.BuildCount, _rebuild.ComputationCount, rebuildMs, textMs, layoutMs,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated, animationMs) : default);
    }

    public int AddInterfaceSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        Group(FrameStage.InterfaceUpdate).Add(system);

    public int AddGameSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        Group(FrameStage.GameInputAndSimulation).Add(system);

    public SystemGroup<ImmediateSystemContext> Animation => Group(FrameStage.Animation);

    public int AddFlipSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        Group(FrameStage.Flip).Add(system);

    /// <summary>Adds a concrete motion to the same animation phases used by the interface.</summary>
    public void AddAnimation<T, TOps, TParameters, TState, TEvaluator>(Spec<T, TOps, TParameters, TState, TEvaluator> spec)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> =>
        AnimationSystems.Add(Group(FrameStage.Animation), spec);

    private SystemGroup<ImmediateSystemContext> Group(FrameStage stage) => _stages[(int)stage].Systems!;

    public void Dispose()
    {
        for (int index = _stages.Length - 1; index >= 0; index--)
            _stages[index].Systems?.Dispose();
    }
}
