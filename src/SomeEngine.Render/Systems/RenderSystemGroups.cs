using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Capability passed only while render preparation owns the exclusive frame boundary. It exposes
/// RenderWorld reads and high-level batch composition, never the storage allocator or mapped
/// buffers.
/// </summary>
public readonly struct RenderPrepareSystemContext
{
    private readonly RenderInstanceStorageSystem? _instances;
    private readonly RenderInstanceWriteScope? _write;
    private readonly RenderPrepareScope? _scope;

    internal RenderPrepareSystemContext(
        RenderWorld world,
        RenderInstanceStorageSystem? instances,
        RenderInstanceWriteScope? write,
        RenderPrepareScope? scope,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        World = world;
        _instances = instances;
        _write = write;
        _scope = scope;
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public RenderWorld World { get; }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

    public RenderInstancePropertyLayout InstanceLayout => RequireInstances().Layout;
    public bool HasInstanceStorage => _instances is not null;

    /// <summary>
    /// Allocates one exact logical batch and returns only its write capability. Classification,
    /// range assignment, producer execution, and job completion remain owned by this system.
    /// </summary>
    public RenderInstanceWriteHandle AllocateBatch(
        RenderInstancePropertyLayout exactLayout,
        int instanceCount) =>
        RequireInstances().AllocateBatch(
            RequireScope(),
            RequireWriteScope(),
            exactLayout,
            instanceCount);

    /// <summary>
    /// Opens declared properties of a live batch for an in-place rewrite. The caller must use the
    /// full exact layout whenever entity membership or row order changed.
    /// Replaced properties must receive a complete binding and every instance value before Publish returns.
    /// </summary>
    public RenderInstanceWriteHandle RewriteBatch(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties = null) =>
        RequireInstances().RewriteBatch(
            RequireScope(),
            RequireWriteScope(),
            batch,
            properties,
            replacedProperties);

    public void Retire(RenderInstanceBatch batch) =>
        RequireInstances().Retire(RequireScope(), RequireWriteScope(), batch);

    // Pipeline assemblies receive this only through explicit friend access. The public system
    // capability remains high-level instance composition; resource owners use the boundary to
    // acquire only their own timelines.
    internal RenderPrepareScope ActiveScope => RequireScope();

    private RenderInstanceStorageSystem RequireInstances() => _instances ??
        throw new InvalidOperationException("No instance storage was installed for these prepare systems.");

    private RenderPrepareScope RequireScope() =>
        _scope ?? throw new InvalidOperationException(
            "Render preparation is not active during this lifecycle callback.");

    private RenderInstanceWriteScope RequireWriteScope() =>
        _write ?? throw new InvalidOperationException(
            "Render preparation is not active during this lifecycle callback.");
}

/// <summary>
/// Read capability passed to frame-consumer systems. It can open an immutable instance-storage
/// view but has no prepare or allocation path.
/// </summary>
public ref struct RenderFrameSystemContext
{
    private readonly RenderInstanceStorageSystem? _instances;
    private readonly RenderFrame? _frame;
    private RenderGraphFrame _graph;
    private bool _hasGraph;
    private uint _lastSystemVersion;
    private uint _currentSystemVersion;

    internal RenderFrameSystemContext(
        RenderWorld world,
        RenderInstanceStorageSystem? instances,
        RenderFrame? frame,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        World = world;
        _instances = instances;
        _frame = frame;
        _graph = default;
        _hasGraph = false;
        _lastSystemVersion = lastSystemVersion;
        _currentSystemVersion = currentSystemVersion;
    }

    internal RenderFrameSystemContext(
        RenderWorld world,
        RenderInstanceStorageSystem? instances,
        RenderFrame frame,
        RenderGraphFrame graph)
    {
        World = world;
        _instances = instances;
        _frame = frame;
        _graph = graph;
        _hasGraph = true;
        _lastSystemVersion = 0;
        _currentSystemVersion = 0;
    }

    public readonly RenderWorld World { get; }

    public readonly uint LastSystemVersion => _lastSystemVersion;

    public readonly uint CurrentSystemVersion => _currentSystemVersion;

    public readonly RenderInstancePropertyLayout InstanceLayout => RequireInstances().Layout;
    public readonly bool HasInstanceStorage => _instances is not null;
    private readonly RenderInstanceStorageSystem RequireInstances() => _instances ??
        throw new InvalidOperationException("No instance storage was installed for these frame systems.");

    /// <summary>
    /// The one linear graph recording shared by every frame system in registration order.
    /// System callbacks may add resources and passes but must not consume or dispose the builder.
    /// </summary>
    public readonly RenderGraphFrame Graph
    {
        get
        {
            if (!_hasGraph)
            {
                throw new InvalidOperationException(
                    "A render graph is not active during this lifecycle callback.");
            }
            return _graph;
        }
    }

    public readonly RenderInstanceStorageView OpenInstances() =>
        RequireInstances().OpenRead(
            _frame ?? throw new InvalidOperationException(
                "A render frame is not active during this lifecycle callback."));

    internal readonly RenderFrame ActiveFrame =>
        _frame ?? throw new InvalidOperationException(
            "A render frame is not active during this lifecycle callback.");

    internal void SetSystemVersion(uint lastSystemVersion, uint currentSystemVersion)
    {
        _lastSystemVersion = lastSystemVersion;
        _currentSystemVersion = currentSystemVersion;
    }
}

/// <summary>
/// Registration-order prepare-system group for one RenderWorld. The group borrows, rather than
/// owns, the instance-storage system so pipelines remain composable instead of becoming one
/// global renderer object.
/// </summary>
public sealed class RenderPrepareSystems : IDisposable
{
    private readonly RenderExtractionSystems _extraction;
    private readonly RenderPrepareDriver _driver;
    private readonly SystemGroup<RenderPrepareSystemContext> _systems;
    private bool _shutdown;

    public RenderPrepareSystems(RenderWorld world, RenderExtractionSystems extraction)
        : this(world, null, extraction) { }

    public RenderPrepareSystems(
        RenderWorld world,
        RenderInstanceStorageSystem? instances,
        RenderExtractionSystems extraction)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(extraction);
        if (instances is not null && !ReferenceEquals(world, instances.World))
        {
            throw new ArgumentException(
                "Prepare systems and instance storage must use the same RenderWorld.",
                nameof(instances));
        }
        if (!ReferenceEquals(world, extraction.World))
        {
            throw new ArgumentException(
                "Prepare systems and extraction must use the same RenderWorld.",
                nameof(extraction));
        }
        _extraction = extraction;
        _driver = new RenderPrepareDriver(world, instances);
        _systems = new SystemGroup<RenderPrepareSystemContext>(_driver);
    }

    public int Count => _systems.Count;

    public int Add<TSystem>(TSystem system)
        where TSystem : ISystem<RenderPrepareSystemContext> =>
        _systems.Add(system);

    public void Enable(int index) => _systems.Enable(index);

    public void Disable(int index) => _systems.Disable(index);

    /// <summary>
    /// Applies the detached extraction candidate without running pipeline preparation. This is
    /// useful when a pipeline must discover newly published RenderWorld assets asynchronously
    /// before the remaining systems update in the same prepare scope.
    /// </summary>
    public bool ApplyExtraction(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ObjectDisposedException.ThrowIf(_shutdown, this);
        _driver.Enter(scope, writeInstances: false);
        try
        {
            return _extraction.ApplyPendingCandidate();
        }
        finally
        {
            _driver.Exit(scope);
        }
    }

    /// <summary>
    /// Removes a system while its destruction callback still has the exclusive prepare
    /// capability. Any batch owned by that system must be retired from OnDestroy.
    /// </summary>
    public void Remove(RenderPrepareScope scope, int index)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ObjectDisposedException.ThrowIf(_shutdown, this);
        _driver.Enter(scope);
        try
        {
            _systems.Remove(index);
        }
        finally
        {
            _driver.Exit(scope);
        }
    }

    public void Update(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ObjectDisposedException.ThrowIf(_shutdown, this);
        _driver.Enter(scope);
        try
        {
            _extraction.ApplyPendingCandidate();
            _systems.Update();
        }
        finally
        {
            _driver.Exit(scope);
        }
    }

    /// <summary>Runs system destruction while the exclusive prepare capability is still valid.</summary>
    public void Shutdown(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_shutdown)
            return;
        _driver.Enter(scope);
        try
        {
            _systems.Dispose();
            _shutdown = true;
        }
        finally
        {
            _driver.Exit(scope);
        }
    }

    public void Dispose()
    {
        if (!_shutdown)
        {
            throw new InvalidOperationException(
                "Render prepare systems must shut down at a prepare boundary before disposal.");
        }
    }

    private sealed class RenderPrepareDriver(
        RenderWorld world,
        RenderInstanceStorageSystem? instances) : ISystemDriver<RenderPrepareSystemContext>
    {
        private RenderInstanceWriteScope? _write;
        private RenderPrepareScope? _scope;

        public uint AcquireSystemVersion(ref SystemSlot slot) => world.AcquireSystemVersion();

        public RenderPrepareSystemContext CreateContext(ref SystemSlot slot) => new(
            world,
            instances,
            _write,
            _scope,
            slot.LastSystemVersion,
            slot.CurrentSystemVersion);

        internal void Enter(RenderPrepareScope scope, bool writeInstances = true)
        {
            if (Interlocked.CompareExchange(ref _scope, scope, null) is not null)
                throw new InvalidOperationException("Render prepare systems are already updating.");
            bool entered = false;
            try
            {
                scope.EnterOperation();
                entered = true;
                // Applying extracted components does not write GPU instance data. Keep that
                // phase independent of writable buffer availability and buffer rotation.
                _write = writeInstances ? instances?.OpenWrite(scope) : null;
            }
            catch
            {
                if (entered) scope.ExitOperation();
                _scope = null;
                throw;
            }
        }

        internal void Exit(RenderPrepareScope scope)
        {
            if (!ReferenceEquals(_scope, scope))
                throw new InvalidOperationException("Render prepare-system ownership was lost.");
            try { _write?.Dispose(); }
            finally
            {
                _write = null;
                scope.ExitOperation();
                _scope = null;
            }
        }
    }
}

/// <summary>Registration-order read-only consumer systems for one RenderWorld frame.</summary>
public sealed class RenderFrameSystems : IDisposable
{
    private readonly RenderFrameDriver _driver;
    private readonly SystemGroup<RenderFrameSystemContext> _systems;

    public RenderFrameSystems(
        RenderWorld world,
        RenderInstanceStorageSystem? instances = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (instances is not null && !ReferenceEquals(world, instances.World))
        {
            throw new ArgumentException(
                "Frame systems and instance storage must use the same RenderWorld.",
                nameof(instances));
        }
        _driver = new RenderFrameDriver(world, instances);
        _systems = new SystemGroup<RenderFrameSystemContext>(_driver);
    }

    public int Count => _systems.Count;

    public int Add<TSystem>(TSystem system)
        where TSystem : ISystem<RenderFrameSystemContext> =>
        _systems.Add(system);

    public void Enable(int index) => _systems.Enable(index);

    public void Disable(int index) => _systems.Disable(index);

    /// <summary>Creates enabled frame systems before retained graph installation.</summary>
    public void Initialize() => _systems.Initialize();

    public void Update(RenderFrame frame, RenderGraphFrame graph)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _driver.Enter(frame);
        try
        {
            RenderFrameSystemContext context = _driver.CreateRecordingContext(graph);
            _systems.Update(ref context);
        }
        finally
        {
            _driver.Exit(frame);
        }
    }

    public void Dispose() => _systems.Dispose();

    private sealed class RenderFrameDriver(
        RenderWorld world,
        RenderInstanceStorageSystem? instances) : ISystemDriver<RenderFrameSystemContext>
    {
        private RenderFrame? _frame;

        public uint AcquireSystemVersion(ref SystemSlot slot) => world.AcquireSystemVersion();

        public RenderFrameSystemContext CreateContext(scoped ref SystemSlot slot) => new(
            world,
            instances,
            _frame,
            slot.LastSystemVersion,
            slot.CurrentSystemVersion);

        public void CreateContext(
            scoped ref SystemSlot slot,
            ref RenderFrameSystemContext context) =>
            context.SetSystemVersion(slot.LastSystemVersion, slot.CurrentSystemVersion);

        internal RenderFrameSystemContext CreateRecordingContext(RenderGraphFrame graph) =>
            new(
                world,
                instances,
                _frame ?? throw new InvalidOperationException("A render frame is not active."),
                graph);

        internal void Enter(RenderFrame frame)
        {
            if (frame.IsClosed) throw new ObjectDisposedException(nameof(RenderFrame));
            if (Interlocked.CompareExchange(ref _frame, frame, null) is not null)
                throw new InvalidOperationException("Render frame systems are already updating.");
        }

        internal void Exit(RenderFrame frame)
        {
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _frame, null, frame), frame))
                throw new InvalidOperationException("Render frame-system ownership was lost.");
        }
    }
}
