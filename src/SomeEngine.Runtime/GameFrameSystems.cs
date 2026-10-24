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
    double TextMs, double LayoutMs, double TotalMs, long AllocatedBytes);

/// <summary>Game-world interface systems, with input before simulation and layout before extraction.</summary>
public sealed class GameFrameSystems : IDisposable
{
    private readonly World _world;
    private readonly (FrameStage Stage, SystemGroup<ImmediateSystemContext>? Systems)[] _stages;
    private readonly ViewRebuildSystem _rebuild = new();
    private static readonly TraceSite RebuildTrace = new("someui", "Views.Rebuild");
    private static readonly TraceSite TextTrace = new("someui", "Views.TextMeasurement");
    private static readonly TraceSite LayoutTrace = new("someui", "Views.Layout");

    public GameFrameSystems(World world)
    {
        _world = world;
        _stages = Enum.GetValues<FrameStage>().Select(stage => (stage,
            stage is FrameStage.PlatformInput or FrameStage.GameInputAndSimulation or FrameStage.Extraction
                ? null : new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world)))).ToArray();
        Group(FrameStage.InterfaceInput).Add(new HitTestSystem());
        Group(FrameStage.InterfaceInput).Add(new FocusNavigationSystem());
        Group(FrameStage.ViewRebuild).Add(_rebuild);
        AnimationSystems.AddDefaults(Group(FrameStage.Animation));
        var shaping = new TextShapingSystem();
        Group(FrameStage.TextMeasurement).Add(shaping);
        Group(FrameStage.Layout).Add(new LayoutSystem(shaping));
        Group(FrameStage.Flip).Add(new FlipSystem());
    }

    /// <summary>Describes actual registration and the surrounding application boundaries.</summary>
    public FrameStageDescription[] Describe() => _stages.Select(stage =>
        new FrameStageDescription(stage.Stage, stage.Systems?.DescribeSystems() ?? [])).ToArray();

    /// <summary>Consumes platform input using the previous layout, before game input runs.</summary>
    public void UpdateInput() => Group(FrameStage.InterfaceInput).Update();

    /// <summary>Runs after simulation; the application extracts the completed result afterwards.</summary>
    public void UpdateViews(double deltaSeconds) => UpdateViewsCore(deltaSeconds, measure: false);

    /// <summary>Runs the same view stages and returns measurement data for the completed frame.</summary>
    public ViewTiming MeasureViews(double deltaSeconds) => UpdateViewsCore(deltaSeconds, measure: true);

    private ViewTiming UpdateViewsCore(double deltaSeconds, bool measure)
    {
        long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        double rebuildMs = 0, textMs = 0, layoutMs = 0;
        var context = new ImmediateSystemContext(_world, 0, 0, deltaSeconds);
        foreach (var stage in _stages)
        {
            if (stage.Stage < FrameStage.ViewRebuild || stage.Systems is null) continue;
            if (measure && stage.Stage == FrameStage.ViewRebuild)
                rebuildMs = Measure(stage.Systems, RebuildTrace, ref context);
            else if (measure && stage.Stage == FrameStage.TextMeasurement)
                textMs = Measure(stage.Systems, TextTrace, ref context);
            else if (measure && stage.Stage == FrameStage.Layout)
                layoutMs = Measure(stage.Systems, LayoutTrace, ref context);
            else
                stage.Systems.Update(ref context);
        }
        return measure ? new ViewTiming(_rebuild.BuildCount, _rebuild.ComputationCount, rebuildMs, textMs, layoutMs,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated) : default;
    }

    private static double Measure(SystemGroup<ImmediateSystemContext> systems, TraceSite site, ref ImmediateSystemContext context)
    {
        long started = Stopwatch.GetTimestamp();
        using var trace = EngineTrace.Begin(site);
        systems.Update(ref context);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public int AddInterfaceSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        Group(FrameStage.InterfaceUpdate).Add(system);

    public int AddFlipSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        Group(FrameStage.Flip).Add(system);

    private SystemGroup<ImmediateSystemContext> Group(FrameStage stage) => _stages[(int)stage].Systems!;

    public void Dispose()
    {
        for (int index = _stages.Length - 1; index >= 0; index--)
            _stages[index].Systems?.Dispose();
    }
}
