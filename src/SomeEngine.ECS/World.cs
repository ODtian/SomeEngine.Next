using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Hooks;

namespace SomeEngine.ECS;

/// <summary>
/// ECS World——实体生命周期与组件操作的公开门面。
/// 内部组合领域 owner，并把领域规则交给对应 owner。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §1.3, §5.3, §5.4, §5.5
/// </remarks>
public partial class World : IDisposable
{
    private readonly Owners.Hooks _hooks;
    private readonly Owners.Commands _commands;
    private readonly WorldStructuralMetricsState _structuralMetrics;
    private WorldStructurePublication _publishedStructure;
    private int _structuralTransactionActive;
    private long _topologyRevision = 1;
    private long _lastStructuralCandidatePublicationEpoch;

    [ThreadStatic]
    private static StructuralCandidateContext? t_candidateContext;

    private Owners.Entities _entities => CurrentStructureRoot.Entities;
    private Owners.Tables _tables => CurrentStructureRoot.Tables;
    private Owners.Sparse _sparse => CurrentStructureRoot.Sparse;
    private Owners.Indices _indices => CurrentStructureRoot.Indices;
    private Owners.RelationGraph _relationGraph => CurrentStructureRoot.RelationGraph;
    private Owners.Components _components => CurrentStructureRoot.Components;
    private Queries.QueryRegistry _queries => CurrentStructureRoot.Queries;
    private Owners.Buffers _buffers => CurrentStructureRoot.Buffers;
    private Owners.Bundles _bundles => CurrentStructureRoot.Bundles;
    private Owners.Copy _copy => CurrentStructureRoot.Copy;
    private Owners.Shared _shared => CurrentStructureRoot.Shared;
    private Owners.Clock _clock => CurrentStructureRoot.Clock;
    private Owners.Iteration _iteration => CurrentStructureRoot.Iteration;
    private Owners.Hierarchy _hierarchy => CurrentStructureRoot.Hierarchy;

    public World(int initialEntityCapacity = 256)
    {
        _hooks = new Owners.Hooks();
        _hooks.Bind(this);
        _commands = new Owners.Commands();
        _structuralMetrics = new WorldStructuralMetricsState();
        WorldStructureRoot root = WorldStructureRoot.Create(
            this,
            initialEntityCapacity,
            _hooks);
        _publishedStructure = new WorldStructurePublication(root, epoch: 0);
    }

    private WorldStructureRoot CurrentStructureRoot
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            StructuralCandidateContext? context = t_candidateContext;
            if (context is null)
                return Volatile.Read(ref _publishedStructure).Root;
            if (ReferenceEquals(context.World, this))
                return context.Root;

            context = FindStructuralCandidate(this, context.Previous);
            return context is null
                ? Volatile.Read(ref _publishedStructure).Root
                : context.Root;
        }
    }

    internal WorldStructureRoot PublishedStructureRoot =>
        Volatile.Read(ref _publishedStructure).Root;

    internal WorldStructureRoot ActiveStructureRoot => CurrentStructureRoot;

    internal bool OwnsStructureRoot(WorldStructureRoot root) =>
        ReferenceEquals(CurrentStructureRoot, root);

    internal long PublishedStructureEpoch =>
        Volatile.Read(ref _publishedStructure).Epoch;

    /// <summary>
    /// Monotonic fact version for every admitted topology-write boundary. Unlike the detached-root
    /// publication epoch this also covers immediate structural mutation paths.
    /// </summary>
    public long PublishedTopologyRevision => Volatile.Read(ref _topologyRevision);

    /// <summary>
    /// Returns a lock-free snapshot of structural candidate prepare, publication, and rollback
    /// costs. Sampling occurs only at structural transaction boundaries.
    /// </summary>
    public WorldStructuralMetrics GetStructuralMetrics() => _structuralMetrics.Snapshot();

    internal WorldStructuralMetricsState StructuralMetrics => _structuralMetrics;

    internal long BumpTopologyRevision()
    {
        long revision = Interlocked.Increment(ref _topologyRevision);
        return revision > 0
            ? revision
            : throw new InvalidOperationException("World topology revision space was exhausted.");
    }

    internal StructuralTransactionScope BeginStructuralTransaction()
    {
        // Unbound World mutations and structural root preparation share this reentrant monitor.
        // A candidate owner may call ordinary World write APIs while preparing, but another thread
        // cannot mutate the still-published source backing until the candidate commits or aborts.
        Monitor.Enter(_unboundMutationGate);
        if (Interlocked.CompareExchange(ref _structuralTransactionActive, 1, 0) != 0)
        {
            Monitor.Exit(_unboundMutationGate);
            throw new InvalidOperationException(
                "Another structural transaction is already active for this World.");
        }

        return new StructuralTransactionScope(this, Environment.CurrentManagedThreadId);
    }

    internal long NextStructureEpoch()
    {
        long current = Volatile.Read(ref _publishedStructure).Epoch;
        if (current == long.MaxValue)
            throw new InvalidOperationException("World structural publication epoch is exhausted.");
        return current + 1;
    }

    internal bool IsStructureEpochPublished(long epoch) =>
        epoch > 0 &&
        Volatile.Read(ref _lastStructuralCandidatePublicationEpoch) >= epoch;

    internal WorldStructurePublication PrepareStructurePublication(
        WorldStructureRoot candidate,
        long epoch)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (epoch != NextStructureEpoch())
            throw new InvalidOperationException("Structural candidate publication epoch is stale.");
        return new WorldStructurePublication(candidate, epoch);
    }

    internal void ThrowIfStructuralTransactionActive()
    {
        if (Volatile.Read(ref _structuralTransactionActive) != 0)
        {
            throw new InvalidOperationException(
                "Stable World registrations cannot be changed during a structural transaction.");
        }
    }

    internal StructuralCandidateScope EnterStructuralCandidate(WorldStructureRoot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (FindStructuralCandidate(this, t_candidateContext) is not null)
        {
            throw new InvalidOperationException(
                "Another structural candidate is already active for this World.");
        }

        var context = new StructuralCandidateContext(this, candidate, t_candidateContext);
        t_candidateContext = context;
        return new StructuralCandidateScope(
            this,
            context,
            Environment.CurrentManagedThreadId);
    }

    internal void PublishStructuralCandidate(WorldStructurePublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        StructuralCandidateContext? context = t_candidateContext;
        if (context is null ||
            !ReferenceEquals(context.World, this) ||
            !ReferenceEquals(context.Root, publication.Root))
        {
            throw new InvalidOperationException(
                "Only the innermost active structural candidate owner may publish its root.");
        }
        if (publication.Epoch != NextStructureEpoch())
            throw new InvalidOperationException("Structural candidate publication epoch is stale.");

        // Root identity and epoch become visible through one release-write. A deferred handle can
        // therefore never resolve against the old root, and readers cannot observe an epoch/root
        // pair assembled from different structural generations.
        Volatile.Write(ref _publishedStructure, publication);
        Volatile.Write(
            ref _lastStructuralCandidatePublicationEpoch,
            publication.Epoch);
        BumpTopologyRevision();
    }

    private void ExitStructuralCandidate(
        StructuralCandidateContext context,
        int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId ||
            !ReferenceEquals(t_candidateContext, context) ||
            !ReferenceEquals(context.World, this))
        {
            throw new InvalidOperationException(
                "Structural candidate scope must be released in nesting order by its owning " +
                "thread.");
        }

        t_candidateContext = context.Previous;
    }

    private void EndStructuralTransaction(int ownerThreadId)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "Structural transaction must be released by its owning thread.");
        }
        if (FindStructuralCandidate(this, t_candidateContext) is not null)
        {
            throw new InvalidOperationException(
                "Structural transaction cannot end while its candidate scope is active.");
        }
        if (!Monitor.IsEntered(_unboundMutationGate))
        {
            throw new InvalidOperationException(
                "Structural transaction lost its unbound mutation gate.");
        }
        if (Interlocked.Exchange(ref _structuralTransactionActive, 0) != 1)
            throw new InvalidOperationException("Structural transaction scope is not active.");
        Monitor.Exit(_unboundMutationGate);
    }

    private static StructuralCandidateContext? FindStructuralCandidate(
        World world,
        StructuralCandidateContext? context)
    {
        while (context is not null)
        {
            if (ReferenceEquals(context.World, world))
                return context;
            context = context.Previous;
        }

        return null;
    }

    internal Owners.Entities Entities => _entities;

    internal Owners.Tables Tables => _tables;

    internal Owners.Sparse Sparse => _sparse;

    internal Owners.Components Components => _components;

    internal Owners.Buffers Buffers => _buffers;

    internal Owners.Indices Indices => _indices;

    internal Owners.RelationGraph RelationGraph => _relationGraph;

    internal Owners.Bundles Bundles => _bundles;

    internal Owners.Copy Copy => _copy;

    internal Owners.Shared Shared => _shared;

    internal Owners.Hierarchy Hierarchy => _hierarchy;

    internal Owners.Clock Clock => _clock;

    internal Owners.Hooks HookStore => _hooks;

    internal object CommandGate => _commands.Gate;

    public CommandBuffer Commands()
    {
        ThrowIfUnavailable();
        ThrowIfJobCommandBufferAccess();
        return _commands.Get(this);
    }

    internal DeferredCommandWriter CommandsFromHook(HookCommandToken token)
    {
        ThrowIfRestrictedWorldApi();
        return _commands.GetHookWriter(this, token);
    }

    internal void EndHookCommandWriter(HookCommandToken token)
    {
        _commands.EndHookWriter(token);
    }

    public void Flush()
    {
        ThrowIfUnavailable();
        ThrowIfJobCommandBufferAccess();
        _hooks.ThrowIfReentrantWorldMutation(writeAccess: true);
        _bundles.ThrowIfReentrantWorldMutation(writeAccess: true);
        lock (_commands.FlushGate)
        {
            if (!_commands.TryReserveNextPlayback(out CommandBuffer? playback))
                return;

            try
            {
                // Do not hold the command gate while admission may wait. The exact wave remains
                // reserved while immediate hooks are free to publish later waves.
                using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
                _commands.PlaybackReservedUnderExistingTopologyAdmission(playback!);
            }
            catch
            {
                _commands.CancelPlaybackReservation(playback!);
                throw;
            }
        }
    }

    internal void BeginCommandOverlay()
    {
        _commands.BeginCandidate();
    }

    internal void EndCommandOverlay(bool published)
    {
        _commands.EndCandidate(published);
    }

    internal void PrepareCommandOverlayPublication()
    {
        _commands.PrepareCandidatePublication();
    }

    internal StructuralMutationScope BeginStructuralMutation()
    {
        return StructuralMutationScope.Begin(this);
    }

    internal sealed class StructuralCandidateContext
    {
        internal StructuralCandidateContext(
            World world,
            WorldStructureRoot root,
            StructuralCandidateContext? previous)
        {
            World = world;
            Root = root;
            Previous = previous;
        }

        internal World World { get; }

        internal WorldStructureRoot Root { get; }

        internal StructuralCandidateContext? Previous { get; }
    }

    internal readonly struct StructuralCandidateScope : IDisposable
    {
        private readonly World? _world;
        private readonly StructuralCandidateContext? _context;
        private readonly int _ownerThreadId;

        internal StructuralCandidateScope(
            World world,
            StructuralCandidateContext context,
            int ownerThreadId)
        {
            _world = world;
            _context = context;
            _ownerThreadId = ownerThreadId;
        }

        public void Dispose()
        {
            if (_world is not null)
                _world.ExitStructuralCandidate(_context!, _ownerThreadId);
        }
    }

    internal readonly struct StructuralTransactionScope : IDisposable
    {
        private readonly World? _world;
        private readonly int _ownerThreadId;

        internal StructuralTransactionScope(World world, int ownerThreadId)
        {
            _world = world;
            _ownerThreadId = ownerThreadId;
        }

        public void Dispose()
        {
            _world?.EndStructuralTransaction(_ownerThreadId);
        }
    }
}

