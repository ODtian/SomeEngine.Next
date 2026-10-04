using SomeEngine.Animation;
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

/// <summary>Game-world interface systems, with input before simulation and layout before extraction.</summary>
public sealed class GameFrameSystems : IDisposable
{
    private readonly World _world;
    private readonly (FrameStage Stage, SystemGroup<ImmediateSystemContext>? Systems)[] _stages;

    public GameFrameSystems(World world)
    {
        _world = world;
        _stages = Enum.GetValues<FrameStage>().Select(stage => (stage,
            stage is FrameStage.PlatformInput or FrameStage.GameInputAndSimulation or FrameStage.Extraction
                ? null : new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world)))).ToArray();
        Group(FrameStage.InterfaceInput).Add(new HitTestSystem());
        Group(FrameStage.InterfaceInput).Add(new FocusNavigationSystem());
        Group(FrameStage.ViewRebuild).Add(new ViewRebuildSystem());
        AnimationSystems.AddDefaults(Group(FrameStage.Animation));
        var shaping = new TextShapingSystem();
        Group(FrameStage.TextMeasurement).Add(shaping);
        Group(FrameStage.Layout).Add(new LayoutSystem(shaping));
    }

    /// <summary>Describes actual registration and the surrounding application boundaries.</summary>
    public FrameStageDescription[] Describe() => _stages.Select(stage =>
        new FrameStageDescription(stage.Stage, stage.Systems?.DescribeSystems() ?? [])).ToArray();

    /// <summary>Consumes platform input using the previous layout, before game input runs.</summary>
    public void UpdateInput() => Group(FrameStage.InterfaceInput).Update();

    /// <summary>Runs after simulation; the application extracts the completed result afterwards.</summary>
    public void UpdateViews(double deltaSeconds)
    {
        var context = new ImmediateSystemContext(_world, 0, 0, deltaSeconds);
        foreach (var stage in _stages)
            if (stage.Stage >= FrameStage.ViewRebuild)
                stage.Systems?.Update(ref context);
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
