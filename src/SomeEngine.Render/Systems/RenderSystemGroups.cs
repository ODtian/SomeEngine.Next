using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Systems;

/// <summary>RenderWorld preparation and completed instance writes for this system update.</summary>
public readonly struct RenderPrepareSystemContext
{
    private readonly RenderInstanceStorageSystem? _instances;
    internal RenderPrepareSystemContext(RenderWorld world, RenderInstanceStorageSystem? instances,
        uint lastSystemVersion, uint currentSystemVersion)
    {
        World = world; _instances = instances;
        LastSystemVersion = lastSystemVersion; CurrentSystemVersion = currentSystemVersion;
    }
    public RenderWorld World { get; }
    public uint LastSystemVersion { get; }
    public uint CurrentSystemVersion { get; }
    public RenderInstancePropertyLayout InstanceLayout => RequireInstances().Layout;
    public bool HasInstanceStorage => _instances is not null;
    public RenderInstanceWriteHandle AllocateBatch(RenderInstancePropertyLayout layout, int instanceCount) =>
        RequireInstances().AllocateBatch(layout, instanceCount);
    public RenderInstanceWriteHandle RewriteBatch(RenderInstanceBatch batch, RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties = null) => RequireInstances().RewriteBatch(batch, properties, replacedProperties);
    public void Retire(RenderInstanceBatch batch) => RequireInstances().Retire(batch);
    private RenderInstanceStorageSystem RequireInstances() => _instances
        ?? throw new InvalidOperationException("These prepare systems have no instance data.");
}

/// <summary>The graph frame and read access to the prepared RenderWorld.</summary>
public ref struct RenderFrameSystemContext
{
    private readonly RenderInstanceStorageSystem? _instances;
    private RenderGraphFrame _graph;
    private readonly bool _hasGraph;
    private uint _lastSystemVersion, _currentSystemVersion;

    internal RenderFrameSystemContext(RenderWorld world, RenderInstanceStorageSystem? instances,
        uint lastSystemVersion, uint currentSystemVersion)
    {
        World = world; _instances = instances; _graph = default; _hasGraph = false;
        _lastSystemVersion = lastSystemVersion; _currentSystemVersion = currentSystemVersion;
    }
    internal RenderFrameSystemContext(RenderWorld world, RenderInstanceStorageSystem? instances, RenderGraphFrame graph)
    {
        World = world; _instances = instances; _graph = graph; _hasGraph = true;
        _lastSystemVersion = 0; _currentSystemVersion = 0;
    }
    public readonly RenderWorld World { get; }
    public readonly uint LastSystemVersion => _lastSystemVersion;
    public readonly uint CurrentSystemVersion => _currentSystemVersion;
    public readonly bool HasInstanceStorage => _instances is not null;
    public readonly RenderInstancePropertyLayout InstanceLayout => RequireInstances().Layout;
    public readonly RenderGraphFrame Graph => _hasGraph ? _graph
        : throw new InvalidOperationException("This lifecycle callback has no graph frame.");
    public readonly RenderInstanceStorageView OpenInstances() => RequireInstances().OpenRead();
    private readonly RenderInstanceStorageSystem RequireInstances() => _instances
        ?? throw new InvalidOperationException("These frame systems have no instance data.");
    internal void SetSystemVersion(uint lastSystemVersion, uint currentSystemVersion)
    { _lastSystemVersion = lastSystemVersion; _currentSystemVersion = currentSystemVersion; }
}

/// <summary>Applies extraction and runs render preparation in registration order.</summary>
public sealed class RenderPrepareSystems : IDisposable
{
    private readonly RenderExtractionSystems _extraction;
    private readonly RenderInstanceStorageSystem? _instances;
    private readonly SystemGroup<RenderPrepareSystemContext> _systems;
    private bool _shutdown;

    public RenderPrepareSystems(RenderWorld world, RenderExtractionSystems extraction) : this(world, null, extraction) { }
    public RenderPrepareSystems(RenderWorld world, RenderInstanceStorageSystem? instances, RenderExtractionSystems extraction)
    {
        _instances = instances; _extraction = extraction;
        _systems = new(new PrepareDriver(world, instances));
    }
    public int Count => _systems.Count;
    public int Add<TSystem>(TSystem system) where TSystem : ISystem<RenderPrepareSystemContext> => _systems.Add(system);
    public void Enable(int index) => _systems.Enable(index);
    public void Disable(int index) => _systems.Disable(index);
    public bool ApplyExtraction() => _extraction.ApplyCollectedChanges();
    public void Remove(int index) => _systems.Remove(index);
    public void Update()
    {
        _extraction.ApplyCollectedChanges();
        _systems.Update();
        _instances?.FinishWrites();
    }
    public void Shutdown()
    {
        if (_shutdown) return;
        _systems.Dispose();
        _shutdown = true;
    }
    public void Dispose() => Shutdown();

    private sealed class PrepareDriver(RenderWorld world, RenderInstanceStorageSystem? instances)
        : ISystemDriver<RenderPrepareSystemContext>
    {
        public uint AcquireSystemVersion(ref SystemSlot system) => world.AcquireSystemVersion();
        public RenderPrepareSystemContext CreateContext(ref SystemSlot system) =>
            new(world, instances, system.LastSystemVersion, system.CurrentSystemVersion);
    }
}

/// <summary>Runs graph consumers after preparation and the shared instance upload.</summary>
public sealed class RenderFrameSystems : IDisposable
{
    private readonly RenderInstanceStorageSystem? _instances;
    private readonly FrameDriver _driver;
    private readonly SystemGroup<RenderFrameSystemContext> _systems;
    public RenderFrameSystems(RenderWorld world, RenderInstanceStorageSystem? instances = null)
    {
        _instances = instances; _driver = new(world, instances); _systems = new(_driver);
    }
    public int Count => _systems.Count;
    public int Add<TSystem>(TSystem system) where TSystem : ISystem<RenderFrameSystemContext> => _systems.Add(system);
    public void Enable(int index) => _systems.Enable(index);
    public void Disable(int index) => _systems.Disable(index);
    public void Initialize() => _systems.Initialize();
    public void Update(RenderGraphFrame graph)
    {
        _instances?.Upload(ref graph);
        RenderFrameSystemContext context = _driver.CreateRecordingContext(graph);
        _systems.Update(ref context);
    }
    public void Dispose() => _systems.Dispose();

    private sealed class FrameDriver(RenderWorld world, RenderInstanceStorageSystem? instances)
        : ISystemDriver<RenderFrameSystemContext>
    {
        public uint AcquireSystemVersion(ref SystemSlot system) => world.AcquireSystemVersion();
        public RenderFrameSystemContext CreateContext(scoped ref SystemSlot system) =>
            new(world, instances, system.LastSystemVersion, system.CurrentSystemVersion);
        public void CreateContext(scoped ref SystemSlot system, ref RenderFrameSystemContext context) =>
            context.SetSystemVersion(system.LastSystemVersion, system.CurrentSystemVersion);
        internal RenderFrameSystemContext CreateRecordingContext(RenderGraphFrame graph) => new(world, instances, graph);
    }
}
