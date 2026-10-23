using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Terrain;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

/// <summary>Installs geometry processing for each run of an application scene.</summary>
public interface IApplicationGeometryFeature
{
    /// <summary>Returns resources to release after the scene's GPU work and systems finish.</summary>
    IDisposable? Install(ApplicationGeometryContext context);
}

/// <summary>Rendering resources and system registration available while a geometry feature is installed.</summary>
public sealed class ApplicationGeometryContext
{
    private readonly RenderFrameSystems _frames;
    private readonly RenderPrepareSystems _preparation;
    private readonly RenderExtractionSystems _extraction;
    private readonly SystemGroup<ImmediateSystemContext> _gameSystems;
    private readonly World _gameWorld;
    private readonly Func<Vector3> _cameraPosition;
    private readonly List<IRenderGraphFrameExecutionObserver> _observers = [];
    private readonly List<IDisposable> _resources = [];
    private readonly List<Action> _gameShutdown = [];
    private RenderTerrainData? _terrainData;

    internal ApplicationGeometryContext(IGraphicsBackend backend, Device device, RenderDevice renderDevice,
        ClusterRenderResources cluster, global::SomeEngine.RenderGraph.RenderGraph graph, World gameWorld,
        RenderWorld renderingWorld, RenderExtractionSystems extraction, RenderPrepareSystems preparation,
        RenderFrameSystems frames, Func<Vector3> cameraPosition)
    {
        Backend = backend;
        Device = device;
        RenderDevice = renderDevice;
        Cluster = cluster;
        Graph = graph;
        RenderingWorld = renderingWorld;
        _gameWorld = gameWorld;
        _gameSystems = new(new ImmediateSystemDriver(gameWorld));
        _extraction = extraction;
        _preparation = preparation;
        _frames = frames;
        _cameraPosition = cameraPosition;
    }

    public IGraphicsBackend Backend { get; }
    public Device Device { get; }
    public RenderDevice RenderDevice { get; }
    public ClusterRenderResources Cluster { get; }
    public global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
    public RenderWorld RenderingWorld { get; }
    /// <summary>The active application's game-side camera position, read during game system updates.</summary>
    public Vector3 CameraPosition => _cameraPosition();

    /// <summary>Runs on the game world before each extraction.</summary>
    public int AddGameSystem<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        _gameSystems.Add(system);

    public void AddExtractor(IRenderExtractionSystem extractor) => _extraction.Add(extractor);

    /// <summary>Runs after extraction and before Cluster residency and instance preparation.</summary>
    public int AddPrepareSystem<TSystem>(TSystem system) where TSystem : ISystem<RenderPrepareSystemContext> =>
        _preparation.Add(system);

    public int AddFrameSystem<TSystem>(TSystem system) where TSystem : ISystem<RenderFrameSystemContext> =>
        _frames.Add(system);

    public void AddCompletionObserver(IRenderGraphFrameExecutionObserver observer) => _observers.Add(observer);

    /// <summary>Runs after submitted GPU work finishes and before game-system jobs are drained.</summary>
    public void AddGameShutdown(Action shutdown) => _gameShutdown.Add(shutdown);

    internal RenderTerrainData GetTerrainData()
    {
        if (_terrainData is not null) return _terrainData;
        _terrainData = new(Backend, Device, Graph);
        AddExtractor(new TerrainRenderExtractor());
        AddPrepareSystem(_terrainData);
        AddFrameSystem(_terrainData);
        AddCompletionObserver(_terrainData);
        _resources.Add(_terrainData);
        return _terrainData;
    }

    internal void Install(IEnumerable<IApplicationGeometryFeature> features)
    {
        foreach (IApplicationGeometryFeature feature in features)
            if (feature.Install(this) is { } resources) _resources.Add(resources);
    }

    internal void DisposeFeatures()
    {
        _gameSystems.Dispose();
        for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        _resources.Clear();
    }

    internal void FinishGame()
    {
        foreach (Action shutdown in _gameShutdown) shutdown();
        _gameShutdown.Clear();
    }

    internal void UpdateGame(double deltaTime)
    {
        var context = new ImmediateSystemContext(_gameWorld, 0, 0, deltaTime);
        _gameSystems.Update(ref context);
    }

    internal void Observe(ref RenderGraphFrame graph)
    {
        foreach (IRenderGraphFrameExecutionObserver observer in _observers) graph.ObserveExecution(observer);
    }
}
