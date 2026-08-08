using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using IComponent = global::SomeEngine.ECS.IComponent;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>A parent-before-child callback for one node in a proven-disjoint dirty subtree.</summary>
public interface IHierarchyPropagationJob<TDomain>
    where TDomain : IHierarchyDomain
{
    void Execute(ref HierarchyPropagationContext<TDomain> context);
}

/// <summary>
/// Scoped hierarchy node access. Writes are restricted to the current node. A read from a
/// writable component family may target only the current node or one of its canonical ancestors;
/// this mechanically excludes another packet root, siblings, and descendants.
/// </summary>
public ref struct HierarchyPropagationContext<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly HierarchyPropagationAccessSet<TDomain> _access;
    private readonly long _stableAddress;

    internal HierarchyPropagationContext(
        HierarchyPropagationAccessSet<TDomain> access,
        Entity entity,
        long stableAddress,
        Entity parent,
        Entity root,
        int depth,
        int packetIndex)
    {
        _access = access;
        _stableAddress = stableAddress;
        Entity = entity;
        Parent = parent;
        Root = root;
        Depth = depth;
        PacketIndex = packetIndex;
    }

    public Entity Entity { get; }

    public Entity Parent { get; }

    public Entity Root { get; }

    public int Depth { get; }

    public int PacketIndex { get; }

    public bool Has<T>() where T : struct => _access.Has<T>(Entity);

    public bool Has<T>(Entity entity) where T : struct => _access.Has<T>(entity);

    public bool IsAlive(Entity entity) => _access.IsAlive(entity);

    public Entity GetParent(Entity entity) => _access.GetParent(entity);

    public T Read<T>() where T : struct, IComponent =>
        _access.Read<T>(Entity, _stableAddress, Entity);

    public T Read<T>(Entity entity) where T : struct, IComponent =>
        _access.Read<T>(Entity, _stableAddress, entity);

    public void Write<T>(in T value)
        where T : struct, IComponent =>
        _access.WriteCurrent(
            Entity,
            _stableAddress,
            in value);
}

/// <summary>One contiguous interval in the stable-sorted normalized root array.</summary>
public readonly struct HierarchyPropagationPacketRange : IEquatable<HierarchyPropagationPacketRange>
{
    internal HierarchyPropagationPacketRange(
        int rootStart,
        int rootCount)
    {
        RootStart = rootStart;
        RootCount = rootCount;
    }

    public int RootStart { get; }

    public int RootCount { get; }

    public bool Equals(HierarchyPropagationPacketRange other) =>
        RootStart == other.RootStart &&
        RootCount == other.RootCount;

    public override bool Equals(object? obj) =>
        obj is HierarchyPropagationPacketRange other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RootStart, RootCount);
}

/// <summary>
/// Public mechanical evidence for normalized roots, deterministic packet ranges, and subtree
/// non-overlap at the captured hierarchy generation.
/// </summary>
public sealed class HierarchyPropagationPartitionProof
{
    private readonly Entity[] _roots;
    private readonly HierarchyPropagationPacketRange[] _ranges;

    internal HierarchyPropagationPartitionProof(
        Entity[] ownedRoots,
        HierarchyPropagationPacketRange[] ownedRanges,
        int rootsPerPacket,
        ulong hierarchyFingerprint,
        long inverseRevision,
        long topologyRevision)
    {
        if (inverseRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(inverseRevision));
        if (topologyRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(topologyRevision));
        ArgumentNullException.ThrowIfNull(ownedRoots);
        ArgumentNullException.ThrowIfNull(ownedRanges);
        _roots = ownedRoots;
        _ranges = ownedRanges;
        RootsPerPacket = rootsPerPacket;
        InverseRevision = inverseRevision;
        TopologyRevision = topologyRevision;
        ValidateRanges(_roots, _ranges, rootsPerPacket);
        Fingerprint = CombineFingerprint(
            _roots,
            _ranges,
            hierarchyFingerprint,
            inverseRevision,
            topologyRevision);
    }

    /// <summary>Stable Entity-identity order after duplicate/dead/covered candidates are removed.</summary>
    public ReadOnlySpan<Entity> NormalizedRoots => _roots;

    public ReadOnlySpan<HierarchyPropagationPacketRange> PacketRanges => _ranges;

    public int RootCount => _roots.Length;

    public int PacketCount => _ranges.Length;

    public int RootsPerPacket { get; }

    public long InverseRevision { get; }

    public long TopologyRevision { get; }

    public ulong Fingerprint { get; }

    public bool ProvesNonOverlap(int firstRoot, int secondRoot)
    {
        if ((uint)firstRoot >= (uint)_roots.Length)
            throw new ArgumentOutOfRangeException(nameof(firstRoot));
        if ((uint)secondRoot >= (uint)_roots.Length)
            throw new ArgumentOutOfRangeException(nameof(secondRoot));
        return firstRoot != secondRoot;
    }

    private static void ValidateRanges(
        ReadOnlySpan<Entity> roots,
        ReadOnlySpan<HierarchyPropagationPacketRange> ranges,
        int rootsPerPacket)
    {
        if (rootsPerPacket <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootsPerPacket));

        var uniqueRoots = new HashSet<Entity>();
        for (int i = 0; i < roots.Length; i++)
        {
            if (roots[i] == Entity.Null || !uniqueRoots.Add(roots[i]))
            {
                throw new InvalidOperationException(
                    "Hierarchy propagation proof roots must be non-null and pairwise distinct.");
            }
        }

        int expectedStart = 0;
        for (int i = 0; i < ranges.Length; i++)
        {
            HierarchyPropagationPacketRange range = ranges[i];
            if (range.RootStart != expectedStart ||
                range.RootCount <= 0 ||
                range.RootCount > rootsPerPacket ||
                range.RootStart > roots.Length - range.RootCount)
            {
                throw new InvalidOperationException(
                    "Hierarchy propagation packets must be positive contiguous root ranges.");
            }
            expectedStart = checked(expectedStart + range.RootCount);
        }

        if (expectedStart != roots.Length)
        {
            throw new InvalidOperationException(
                "Hierarchy propagation packet ranges must cover every normalized root exactly once.");
        }
    }

    private static ulong CombineFingerprint(
        ReadOnlySpan<Entity> roots,
        ReadOnlySpan<HierarchyPropagationPacketRange> ranges,
        ulong hierarchyFingerprint,
        long inverseRevision,
        long topologyRevision)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = (offset ^ hierarchyFingerprint) * prime;
        hash = (hash ^ (ulong)inverseRevision) * prime;
        hash = (hash ^ (ulong)topologyRevision) * prime;
        for (int i = 0; i < roots.Length; i++)
        {
            hash = (hash ^ (uint)roots[i].Index) * prime;
            hash = (hash ^ (uint)roots[i].Generation) * prime;
        }
        for (int i = 0; i < ranges.Length; i++)
        {
            hash = (hash ^ (uint)ranges[i].RootStart) * prime;
            hash = (hash ^ (uint)ranges[i].RootCount) * prime;
        }
        return hash;
    }
}

/// <summary>Explicit deterministic hierarchy propagation grain.</summary>
public readonly struct HierarchyPropagationScheduleOptions
{
    private readonly int _rootsPerPacket;

    public HierarchyPropagationScheduleOptions(
        int rootsPerPacket,
        JobScheduleOptions jobOptions = default)
    {
        if (rootsPerPacket <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootsPerPacket));
        _rootsPerPacket = rootsPerPacket;
        JobOptions = jobOptions;
    }

    /// <summary>Default is one disjoint subtree root per work item.</summary>
    public int RootsPerPacket => _rootsPerPacket == 0 ? 1 : _rootsPerPacket;

    public JobScheduleOptions JobOptions { get; }
}

/// <summary>Asynchronous propagation completion and its post-completion partition evidence.</summary>
public sealed class HierarchyPropagation
{
    private readonly HierarchyPropagationState _state;

    internal HierarchyPropagation(HierarchyPropagationState state, JobHandle handle)
    {
        _state = state;
        Handle = handle;
    }

    public JobHandle Handle { get; }

    public HierarchyPropagationPartitionProof Partition
    {
        get
        {
            if (!Handle.IsCompleted)
            {
                throw new InvalidOperationException(
                    "Hierarchy propagation proof is available after the propagation handle completes.");
            }
            Handle.Complete();
            return _state.RequireProof();
        }
    }

    public HierarchyPropagationPartitionProof GetPartition()
    {
        Handle.Complete();
        return _state.RequireProof();
    }
}

/// <summary>
/// Derives disjoint dirty roots and propagates their non-overlapping subtrees in parallel.
/// </summary>
public static partial class HierarchyPropagationAdapter<TDomain>
    where TDomain : IHierarchyDomain
{
    /// <summary>The exact public grain formula; there is no worker-count or size threshold.</summary>
    public static int CalculatePacketCount(int disjointRootCount, int rootsPerPacket)
    {
        if (disjointRootCount < 0)
            throw new ArgumentOutOfRangeException(nameof(disjointRootCount));
        if (rootsPerPacket <= 0)
            throw new ArgumentOutOfRangeException(nameof(rootsPerPacket));
        return disjointRootCount == 0
            ? 0
            : checked(((disjointRootCount - 1) / rootsPerPacket) + 1);
    }

    public static HierarchyPropagation Schedule<TJob>(
        World world,
        ReadOnlySpan<Entity> dirtyCandidates,
        in TJob job,
        HierarchyMaintenanceDependency<TDomain> maintenance,
        ReadOnlySpan<JobResourceAccess> accesses = default,
        HierarchyPropagationScheduleOptions options = default)
        where TJob : unmanaged, IHierarchyPropagationJob<TDomain>
    {
        ArgumentNullException.ThrowIfNull(world);
        maintenance.RequireWorld(world);
        ValidateDataAccessAliases(world, accesses);

        var state = new HierarchyPropagationState();
        var owner = new PropagationOwnerJob<TJob>(
            world,
            dirtyCandidates.ToArray(),
            job,
            accesses.ToArray(),
            options,
            state,
            maintenance);
        Span<JobResourceAccess> ownerAccesses = stackalloc JobResourceAccess[2];
        ownerAccesses[0] = HierarchyJobAccess<TDomain>.ParentRead(world);
        ownerAccesses[1] = RelationshipJobAccess.TopologyRead(world);
        JobHandle handle = JobSystem.Schedule(
            owner,
            ownerAccesses,
            options.JobOptions,
            maintenance.Handle);
        return new HierarchyPropagation(state, handle);
    }

    private readonly struct PropagationOwnerJob<TJob> : IJob
        where TJob : unmanaged, IHierarchyPropagationJob<TDomain>
    {
        private readonly World _world;
        private readonly ReadOnlyMemory<Entity> _dirtyCandidates;
        private readonly TJob _job;
        private readonly ReadOnlyMemory<JobResourceAccess> _userAccesses;
        private readonly HierarchyPropagationScheduleOptions _options;
        private readonly HierarchyPropagationState _state;
        private readonly HierarchyMaintenanceDependency<TDomain> _maintenance;

        internal PropagationOwnerJob(
            World world,
            ReadOnlyMemory<Entity> dirtyCandidates,
            in TJob job,
            ReadOnlyMemory<JobResourceAccess> userAccesses,
            HierarchyPropagationScheduleOptions options,
            HierarchyPropagationState state,
            HierarchyMaintenanceDependency<TDomain> maintenance)
        {
            _world = world;
            _dirtyCandidates = dirtyCandidates;
            _job = job;
            _userAccesses = userAccesses;
            _options = options;
            _state = state;
            _maintenance = maintenance;
        }

        public void Execute()
        {
            long inverseRevision = _maintenance.RequireFresh(_world);
            long topologyRevision = _world.PublishedTopologyRevision;
            AdmittedHierarchyReader hierarchy = AdmittedHierarchyReader.Capture(_world);
            Entity[] roots = NormalizeRoots(in hierarchy, _dirtyCandidates.Span);
            HierarchyPropagationPacketRange[] ranges = CreateRanges(
                roots.Length,
                _options.RootsPerPacket);
            HierarchyTraversalCapture traversal = CaptureTraversal(
                in hierarchy,
                roots,
                ranges);
            var proof = new HierarchyPropagationPartitionProof(
                roots,
                ranges,
                _options.RootsPerPacket,
                traversal.Fingerprint,
                inverseRevision,
                topologyRevision);
            _state.SetProof(proof);
            JobResourceAccess[] dataAccesses = BuildDataAccesses(
                _world,
                _userAccesses.Span,
                in hierarchy,
                in traversal,
                out HierarchyPropagationComponentCapability[] componentCapabilities);
            if (proof.PacketCount == 0)
                return;

            var accessSet = new HierarchyPropagationAccessSet<TDomain>(
                _world,
                componentCapabilities,
                traversal.EntityAddresses);
            var packets = new PropagationPacketJob<TJob>(
                traversal.PacketNodes,
                _job,
                accessSet,
                inverseRevision,
                topologyRevision);
            JobSystem.ScheduleParallel(
                packets,
                proof.PacketCount,
                batchSize: 1,
                dataAccesses,
                _options.JobOptions);
        }
    }

    private readonly struct PropagationPacketJob<TJob> : IJobParallelFor
        where TJob : unmanaged, IHierarchyPropagationJob<TDomain>
    {
        private readonly ReadOnlyMemory<ReadOnlyMemory<TraversalNode>> _packetNodes;
        private readonly TJob _job;
        private readonly HierarchyPropagationAccessSet<TDomain> _access;
        private readonly long _inverseRevision;
        private readonly long _topologyRevision;

        internal PropagationPacketJob(
            ReadOnlyMemory<ReadOnlyMemory<TraversalNode>> packetNodes,
            in TJob job,
            HierarchyPropagationAccessSet<TDomain> access,
            long inverseRevision,
            long topologyRevision)
        {
            _packetNodes = packetNodes;
            _job = job;
            _access = access;
            _inverseRevision = inverseRevision;
            _topologyRevision = topologyRevision;
        }

        public void Execute(int index)
        {
            _access.RequireCapturedHierarchy(_inverseRevision, _topologyRevision);
            TJob state = _job;
            ReadOnlySpan<TraversalNode> nodes = _packetNodes.Span[index].Span;
            for (int i = 0; i < nodes.Length; i++)
            {
                TraversalNode node = nodes[i];
                var context = new HierarchyPropagationContext<TDomain>(
                    _access,
                    node.Entity,
                    node.StableAddress,
                    node.Parent,
                    node.Root,
                    node.Depth,
                    index);
                using RestrictedWorldApiScope restrictedWorldApi =
                    World.EnterRestrictedWorldApi(
                        RestrictedWorldApiContext.HierarchyPropagation);
                state.Execute(ref context);
            }
        }
    }

    private static Entity[] NormalizeRoots(
        in AdmittedHierarchyReader hierarchy,
        ReadOnlySpan<Entity> dirtyCandidates)
    {
        var candidates = new HashSet<Entity>();
        for (int i = 0; i < dirtyCandidates.Length; i++)
        {
            Entity entity = dirtyCandidates[i];
            if (entity != Entity.Null && hierarchy.IsAlive(entity))
                candidates.Add(entity);
        }

        Entity[] ordered = candidates.ToArray();
        Array.Sort(ordered, EntityIdentityComparer.Instance);
        var roots = new List<Entity>(ordered.Length);
        var chain = new HashSet<Entity>();
        for (int i = 0; i < ordered.Length; i++)
        {
            Entity entity = ordered[i];
            Entity ancestor = hierarchy.GetParent(entity);
            chain.Clear();
            chain.Add(entity);
            bool covered = false;
            while (ancestor != Entity.Null)
            {
                if (!hierarchy.IsAlive(ancestor))
                {
                    throw new InvalidOperationException(
                        $"Hierarchy propagation encountered dead ancestor {ancestor}.");
                }
                if (!chain.Add(ancestor))
                    throw new InvalidOperationException("Hierarchy propagation encountered a cycle.");
                if (candidates.Contains(ancestor))
                {
                    covered = true;
                    break;
                }
                ancestor = hierarchy.GetParent(ancestor);
            }

            if (!covered)
                roots.Add(entity);
        }
        return roots.ToArray();
    }

    private static HierarchyTraversalCapture CaptureTraversal(
        in AdmittedHierarchyReader hierarchy,
        ReadOnlySpan<Entity> roots,
        ReadOnlySpan<HierarchyPropagationPacketRange> ranges)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong fingerprint = offset;
        var owners = new Dictionary<Entity, int>();
        var entityAddresses = new Dictionary<Entity, long>();
        var currentAddresses = new HashSet<long>();
        var currentEntityAddresses = new List<HierarchyPropagationEntityAddress>();
        var packetBuilders = new List<TraversalNode>[ranges.Length];
        for (int packetIndex = 0; packetIndex < packetBuilders.Length; packetIndex++)
            packetBuilders[packetIndex] = new List<TraversalNode>();
        var stack = new Stack<TraversalNode>();
        int currentPacket = 0;

        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            while (rootIndex >=
                   ranges[currentPacket].RootStart + ranges[currentPacket].RootCount)
            {
                currentPacket++;
            }

            Entity root = roots[rootIndex];
            List<TraversalNode> nodes = packetBuilders[currentPacket];
            stack.Push(new TraversalNode(
                root,
                hierarchy.StableAddress(root),
                hierarchy.GetParent(root),
                root,
                depth: 0));
            while (stack.Count != 0)
            {
                TraversalNode node = stack.Pop();
                if (owners.TryGetValue(node.Entity, out int owner))
                {
                    throw new InvalidOperationException(
                        $"Hierarchy roots {owner} and {rootIndex} overlap at {node.Entity}.");
                }
                owners.Add(node.Entity, rootIndex);
                if (!entityAddresses.TryAdd(node.Entity, node.StableAddress) &&
                    entityAddresses[node.Entity] != node.StableAddress)
                {
                    throw new InvalidOperationException(
                        "A hierarchy entity resolved to more than one stable row address.");
                }
                if (!currentAddresses.Add(node.StableAddress))
                {
                    throw new InvalidOperationException(
                        "Hierarchy propagation current nodes must occupy distinct stable rows.");
                }
                currentEntityAddresses.Add(
                    new HierarchyPropagationEntityAddress(node.Entity, node.StableAddress));
                nodes.Add(node);
                fingerprint = (fingerprint ^ (uint)rootIndex) * prime;
                fingerprint = (fingerprint ^ (uint)node.Entity.Index) * prime;
                fingerprint = (fingerprint ^ (uint)node.Entity.Generation) * prime;
                fingerprint = (fingerprint ^ (uint)node.Parent.Index) * prime;
                fingerprint = (fingerprint ^ (uint)node.Parent.Generation) * prime;
                fingerprint = (fingerprint ^ (uint)node.Depth) * prime;

                ReadOnlySpan<Entity> children = hierarchy.GetChildren(node.Entity).Span;
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    Entity child = children[i];
                    if (!hierarchy.IsAlive(child) ||
                        hierarchy.GetParent(child) != node.Entity)
                    {
                        throw new InvalidOperationException(
                            "The maintenance dependency did not publish a fresh canonical hierarchy image.");
                    }
                    stack.Push(new TraversalNode(
                        child,
                        hierarchy.StableAddress(child),
                        node.Entity,
                        root,
                    checked(node.Depth + 1)));
                }
            }
        }

        var packetNodes = new ReadOnlyMemory<TraversalNode>[ranges.Length];
        for (int packetIndex = 0; packetIndex < ranges.Length; packetIndex++)
            packetNodes[packetIndex] = packetBuilders[packetIndex].ToArray();

        // A writable family may be read from canonical ancestors above a selected dirty root.
        // Those rows are outside the write proof, so capture them explicitly for read-only range
        // admission. Internal ancestors are already present in currentAddresses and are covered
        // by the corresponding write ranges.
        var externalAncestorAddresses = new HashSet<long>();
        var externalAncestorEntityAddresses = new List<HierarchyPropagationEntityAddress>();
        var ancestorChain = new HashSet<Entity>();
        for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
        {
            Entity ancestor = hierarchy.GetParent(roots[rootIndex]);
            ancestorChain.Clear();
            while (ancestor != Entity.Null)
            {
                if (!hierarchy.IsAlive(ancestor))
                {
                    throw new InvalidOperationException(
                        $"Hierarchy propagation encountered dead ancestor {ancestor}.");
                }
                if (!ancestorChain.Add(ancestor))
                    throw new InvalidOperationException("Hierarchy propagation encountered a cycle.");

                long address = hierarchy.StableAddress(ancestor);
                if (!entityAddresses.TryAdd(ancestor, address) &&
                    entityAddresses[ancestor] != address)
                {
                    throw new InvalidOperationException(
                        "A hierarchy ancestor resolved to more than one stable row address.");
                }
                if (!currentAddresses.Contains(address) &&
                    externalAncestorAddresses.Add(address))
                {
                    externalAncestorEntityAddresses.Add(
                        new HierarchyPropagationEntityAddress(ancestor, address));
                }
                ancestor = hierarchy.GetParent(ancestor);
            }
        }

        HierarchyPropagationEntityAddress[] currentEntityAddressArray =
            currentEntityAddresses.ToArray();
        HierarchyPropagationEntityAddress[] externalAncestorEntityAddressArray =
            externalAncestorEntityAddresses.ToArray();
        Array.Sort(
            currentEntityAddressArray,
            static (left, right) => left.Address.CompareTo(right.Address));
        Array.Sort(
            externalAncestorEntityAddressArray,
            static (left, right) => left.Address.CompareTo(right.Address));
        var entityAddressArray = new HierarchyPropagationEntityAddress[entityAddresses.Count];
        int addressIndex = 0;
        foreach (KeyValuePair<Entity, long> pair in entityAddresses)
        {
            entityAddressArray[addressIndex++] =
                new HierarchyPropagationEntityAddress(pair.Key, pair.Value);
        }
        Array.Sort(
            entityAddressArray,
            static (left, right) =>
                EntityIdentityComparer.Instance.Compare(left.Entity, right.Entity));

        return new HierarchyTraversalCapture(
            packetNodes,
            entityAddressArray,
            currentEntityAddressArray,
            externalAncestorEntityAddressArray,
            fingerprint);
    }

    /// <summary>
    /// The propagation owner already holds both logical read capabilities. Validate them once,
    /// then pin all normalization and capture reads to the same published structure root instead
    /// of entering one World admission scope per node and per ancestor edge.
    /// </summary>
    private readonly struct AdmittedHierarchyReader
    {
        private readonly WorldStructureRoot _root;
        private readonly HierarchyDomainStore<TDomain>? _domain;

        private AdmittedHierarchyReader(
            WorldStructureRoot root,
            HierarchyDomainStore<TDomain>? domain)
        {
            _root = root;
            _domain = domain;
        }

        internal static AdmittedHierarchyReader Capture(World world)
        {
            JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(world));
            JobSystem.RequireCurrentAccess(HierarchyJobAccess<TDomain>.ParentRead(world));
            WorldStructureRoot root = world.ActiveStructureRoot;
            root.Hierarchy.TryDomain<TDomain>(out HierarchyDomainStore<TDomain> domain);
            return new AdmittedHierarchyReader(root, domain);
        }

        internal bool IsAlive(Entity entity) => _root.Entities.Alive(entity);

        internal bool HasComponent(Entity entity, int componentId)
        {
            EntityRecord record = _root.Entities.ReadRow(entity);
            return record.Archetype is not null &&
                   record.Archetype.HasComponent(componentId);
        }

        internal Entity GetParent(Entity entity) =>
            _domain is null ? Entity.Null : _domain.GetParent(entity);

        internal HierarchyChildrenSnapshot<TDomain> GetChildren(Entity entity) =>
            _domain is null
                ? new HierarchyChildrenSnapshot<TDomain>(Array.Empty<Entity>(), generation: 0)
                : _domain.GetChildren(entity);

        internal long StableAddress(Entity entity)
        {
            EntityRecord record = _root.Entities.ReadRow(entity);
            Chunk chunk = record.Chunk
                ?? throw new InvalidOperationException(
                    $"Hierarchy entity {entity} has no stable chunk row.");
            if ((uint)record.RowInChunk >= (uint)chunk.Count ||
                chunk.PersistentIdentity <= 0)
            {
                throw new InvalidOperationException(
                    $"Hierarchy entity {entity} has an invalid stable chunk row.");
            }

            var range = new StableQueryPacketRange(
                chunk.PersistentIdentity,
                record.RowInChunk,
                rowCount: 1,
                chunk.Count);
            return StableQueryPacketAddress.Address(in range);
        }
    }

    private static HierarchyPropagationPacketRange[] CreateRanges(
        int rootCount,
        int rootsPerPacket)
    {
        int packetCount = CalculatePacketCount(rootCount, rootsPerPacket);
        var ranges = new HierarchyPropagationPacketRange[packetCount];
        for (int packetIndex = 0; packetIndex < packetCount; packetIndex++)
        {
            int start = checked(packetIndex * rootsPerPacket);
            int count = Math.Min(rootsPerPacket, rootCount - start);
            ranges[packetIndex] = new HierarchyPropagationPacketRange(
                start,
                count);
        }
        return ranges;
    }

    private static JobResourceAccess[] BuildDataAccesses(
        World world,
        ReadOnlySpan<JobResourceAccess> userAccesses,
        in AdmittedHierarchyReader hierarchy,
        in HierarchyTraversalCapture traversal,
        out HierarchyPropagationComponentCapability[] componentCapabilities)
    {
        JobResourceAccess topology = RelationshipJobAccess.TopologyRead(world);
        JobResourceAccess parent = HierarchyJobAccess<TDomain>.ParentRead(world);
        var result = new List<JobResourceAccess>(userAccesses.Length + 2)
        {
            topology,
            parent,
        };
        var components = new List<HierarchyPropagationComponentCapability>();
        for (int i = 0; i < userAccesses.Length; i++)
        {
            JobResourceAccess access = userAccesses[i];
            if (SameResource(access, topology) || SameResource(access, parent))
            {
                throw new InvalidOperationException(
                    "Hierarchy propagation packet accesses must contain data storage only.");
            }

            if (WorldStorageJobResources.TryDescribe(world, access, out var storage))
            {
                if (storage.Kind != WorldStorageKind.Table)
                {
                    throw new InvalidOperationException(
                        "Hierarchy propagation supports table-component World storage only; buffer, sparse, and topology resources require a dedicated scoped adapter.");
                }
                if (access.HasRange)
                {
                    throw new InvalidOperationException(
                        "Hierarchy propagation component access must cover the whole family because node rows are discovered after topology capture.");
                }

                ref readonly ComponentInfo info = ref ComponentRegistry.Get(storage.ComponentId);
                RequireJobAliasFree(in info);

                bool writes = access.Mode is JobAccessMode.Write or JobAccessMode.Exclusive;
                if (writes && (info.IsRelationshipSource || info.IsRelationshipTarget))
                {
                    throw new InvalidOperationException(
                        $"Parallel hierarchy propagation cannot write relationship component {info.Type.Name}.");
                }
                if (writes && world.HasValueReplaceHookCallbacks(storage.ComponentId))
                {
                    throw new InvalidOperationException(
                        $"Parallel hierarchy propagation cannot write {info.Type.Name} because its value-replacement path has synchronous callbacks.");
                }
                AddComponentCapability(
                    components,
                    storage.ComponentId,
                    access,
                    writes);
                // Whole-family table accesses authorize the callback surface, but writable
                // families are compiled to captured stable-row ranges below. Retaining the whole
                // declaration here would erase the non-overlap proof and serialize unrelated
                // subtrees in the same component family.
                continue;
            }

            AddNormalized(result, access);
        }

        components.Sort(static (left, right) =>
            left.ComponentId.CompareTo(right.ComponentId));
        componentCapabilities = components.ToArray();
        for (int i = 0; i < componentCapabilities.Length; i++)
        {
            HierarchyPropagationComponentCapability capability = componentCapabilities[i];
            if (!capability.CanWrite)
            {
                AddNormalized(result, capability.Access);
                continue;
            }

            AddStableAddressRanges(
                result,
                world,
                capability.ComponentId,
                in hierarchy,
                traversal.CurrentEntityAddresses.Span,
                write: true);
            AddStableAddressRanges(
                result,
                world,
                capability.ComponentId,
                in hierarchy,
                traversal.ExternalAncestorEntityAddresses.Span,
                write: false);
        }
        return result.ToArray();
    }

    private static void AddStableAddressRanges(
        List<JobResourceAccess> accesses,
        World world,
        int componentId,
        in AdmittedHierarchyReader hierarchy,
        ReadOnlySpan<HierarchyPropagationEntityAddress> sortedAddresses,
        bool write)
    {
        if (sortedAddresses.Length == 0)
            return;

        var key = new WorldStorageResourceKey(WorldStorageKind.Table, componentId);
        bool hasRange = false;
        long start = 0;
        long previous = 0;
        long previousCandidate = -1;
        for (int i = 0; i < sortedAddresses.Length; i++)
        {
            HierarchyPropagationEntityAddress candidate = sortedAddresses[i];
            long current = candidate.Address;
            if (current < 0)
            {
                throw new InvalidOperationException(
                    "A stable component row address cannot be negative.");
            }
            if (i != 0 && current <= previousCandidate)
            {
                throw new InvalidOperationException(
                    "Stable component row addresses must be strictly increasing.");
            }
            previousCandidate = current;

            // The whole-family declaration authorizes T, but a row that does not contain T can
            // never be touched by Read/Replace. Omitting it here preserves exact same-family
            // concurrency without changing the callback's capability surface.
            if (!hierarchy.HasComponent(candidate.Entity, componentId))
                continue;

            if (!hasRange)
            {
                start = previous = current;
                hasRange = true;
                continue;
            }
            if (previous != long.MaxValue && current == previous + 1)
            {
                previous = current;
                continue;
            }

            long length = checked(previous - start + 1);
            AddNormalized(
                accesses,
                write
                    ? WorldStorageJobResources.Write(world, key, start, length)
                    : WorldStorageJobResources.Read(world, key, start, length));
            start = previous = current;
        }

        if (!hasRange)
            return;

        long finalLength = checked(previous - start + 1);
        AddNormalized(
            accesses,
            write
                ? WorldStorageJobResources.Write(world, key, start, finalLength)
                : WorldStorageJobResources.Read(world, key, start, finalLength));
    }

    private static void ValidateDataAccessAliases(
        World world,
        ReadOnlySpan<JobResourceAccess> userAccesses)
    {
        for (int i = 0; i < userAccesses.Length; i++)
        {
            if (!WorldStorageJobResources.TryDescribe(world, userAccesses[i], out var storage) ||
                storage.Kind != WorldStorageKind.Table)
            {
                continue;
            }

            ref readonly ComponentInfo info = ref ComponentRegistry.Get(storage.ComponentId);
            RequireJobAliasFree(in info);
        }
    }

    private static void RequireJobAliasFree(in ComponentInfo info)
    {
        if (info.IsJobAliasFree)
            return;

        throw new InvalidOperationException(
            $"Parallel hierarchy propagation cannot access {info.Type.Name}: direct " +
            "component storage must be alias-free and cannot contain managed references, " +
            "byrefs, pointers, native-sized handles, or recursive external aliases.");
    }

    private static void AddComponentCapability(
        List<HierarchyPropagationComponentCapability> capabilities,
        int componentId,
        JobResourceAccess access,
        bool canWrite)
    {
        for (int i = 0; i < capabilities.Count; i++)
        {
            HierarchyPropagationComponentCapability existing = capabilities[i];
            if (existing.ComponentId != componentId)
                continue;

            if (access.Covers(existing.Access))
            {
                capabilities[i] = new HierarchyPropagationComponentCapability(
                    componentId,
                    access,
                    existing.CanWrite || canWrite);
            }
            return;
        }

        capabilities.Add(new HierarchyPropagationComponentCapability(
            componentId,
            access,
            canWrite));
    }

    private static bool SameResource(JobResourceAccess left, JobResourceAccess right) =>
        left.Kind == right.Kind &&
        left.Id == right.Id &&
        left.Version == right.Version &&
        left.Generation == right.Generation;

    private static void AddNormalized(
        List<JobResourceAccess> accesses,
        JobResourceAccess candidate)
    {
        for (int i = 0; i < accesses.Count; i++)
        {
            JobResourceAccess existing = accesses[i];
            if (existing.Covers(candidate))
            {
                return;
            }
            if (candidate.Covers(existing))
            {
                accesses[i] = candidate;
                for (int covered = accesses.Count - 1; covered > i; covered--)
                {
                    if (candidate.Covers(accesses[covered]))
                        accesses.RemoveAt(covered);
                }
                return;
            }
        }
        accesses.Add(candidate);
    }

    private sealed class EntityIdentityComparer : IComparer<Entity>
    {
        internal static readonly EntityIdentityComparer Instance = new();

        public int Compare(Entity left, Entity right)
        {
            int index = left.Index.CompareTo(right.Index);
            return index != 0 ? index : left.Generation.CompareTo(right.Generation);
        }
    }
}

internal sealed class HierarchyPropagationState
{
    private HierarchyPropagationPartitionProof? _proof;

    internal void SetProof(HierarchyPropagationPartitionProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        Volatile.Write(ref _proof, proof);
    }

    internal HierarchyPropagationPartitionProof RequireProof()
    {
        return Volatile.Read(ref _proof)
            ?? throw new InvalidOperationException(
                "Hierarchy propagation did not produce partition evidence.");
    }
}

internal sealed class HierarchyPropagationAccessSet<TDomain>
    where TDomain : IHierarchyDomain
{
    private readonly World _world;
    private readonly ReadOnlyMemory<HierarchyPropagationComponentCapability> _components;
    private readonly ReadOnlyMemory<HierarchyPropagationEntityAddress> _entityAddresses;
    private readonly HierarchyPropagationExecutionVersion _executionVersion = new();

    internal HierarchyPropagationAccessSet(
        World world,
        ReadOnlyMemory<HierarchyPropagationComponentCapability> components,
        ReadOnlyMemory<HierarchyPropagationEntityAddress> entityAddresses)
    {
        _world = world;
        _components = components;
        _entityAddresses = entityAddresses;
    }

    internal bool Has<T>(Entity entity)
        where T : struct =>
        _world.ActiveStructureRoot.Components.Has<T>(entity);

    internal bool IsAlive(Entity entity) =>
        _world.ActiveStructureRoot.Entities.Alive(entity);

    internal Entity GetParent(Entity entity)
    {
        return _world.ActiveStructureRoot.Hierarchy.TryDomain<TDomain>(out var store)
            ? store.GetParent(entity)
            : Entity.Null;
    }

    internal void RequireCapturedHierarchy(long inverseRevision, long topologyRevision)
    {
        JobSystem.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(_world));
        JobSystem.RequireCurrentAccess(HierarchyJobAccess<TDomain>.ParentRead(_world));
        if (_world.PublishedTopologyRevision != topologyRevision ||
            !_world.ActiveStructureRoot.Hierarchy.TryDomain<TDomain>(out var store) ||
            !store.IsInverseFresh ||
            store.InverseRevision != inverseRevision)
        {
            throw new InvalidOperationException(
                "Hierarchy propagation capture became stale before packet execution; no packet writes were applied.");
        }
    }

    internal T Read<T>(Entity current, long currentAddress, Entity target)
        where T : struct, IComponent
    {
        HierarchyPropagationComponentCapability capability =
            RequireCapability<T>(requireWrite: false);
        if (capability.CanWrite)
        {
            if (target != current && !IsAncestor(target, current))
            {
                throw new InvalidOperationException(
                    $"Writable component family {typeof(T).Name} may be read only from the current hierarchy node or one of its canonical ancestors.");
            }

            RequireComponentPresence<T>(target);

            long targetAddress = target == current
                ? currentAddress
                : CapturedAddress(target);
            JobSystem.RequireCurrentAccess(
                ComponentRangeAccess(capability.ComponentId, targetAddress, write: false));
        }
        else
            JobSystem.RequireCurrentAccess(capability.Access);
        return _world.ActiveStructureRoot.Components.Read<T>(target);
    }

    internal void WriteCurrent<T>(
        Entity current,
        long currentAddress,
        in T value)
        where T : struct, IComponent
    {
        if (ComponentMetadata<T>.IsRelationshipSource ||
            ComponentMetadata<T>.IsRelationshipTarget)
        {
            throw new InvalidOperationException(
                "Hierarchy propagation cannot write canonical or derived relationship components.");
        }

        HierarchyPropagationComponentCapability capability =
            RequireCapability<T>(requireWrite: true);
        RequireComponentPresence<T>(current);
        JobSystem.RequireCurrentAccess(
            ComponentRangeAccess(capability.ComponentId, currentAddress, write: true));

        // The complete stable-range owner is admitted before any packet executes. Acquire the
        // shared version only after every per-write guard succeeds and the callback is about to
        // mutate World storage. Read-only callbacks and writable callbacks that never call Write
        // therefore do not publish a false change epoch.
        uint executionVersion = _executionVersion.Get(_world);
        _world.ActiveStructureRoot.Components.Replace(
            current,
            in value,
            executionVersion);
    }

    private void RequireComponentPresence<T>(Entity entity)
        where T : struct, IComponent
    {
        if (!_world.ActiveStructureRoot.Components.Has<T>(entity))
        {
            throw new InvalidOperationException(
                $"Entity {entity} does not have component {typeof(T).Name}.");
        }
    }

    private HierarchyPropagationComponentCapability RequireCapability<T>(bool requireWrite)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ReadOnlySpan<HierarchyPropagationComponentCapability> components =
            _components.Span;
        int low = 0;
        int high = components.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            HierarchyPropagationComponentCapability capability = components[middle];
            int order = capability.ComponentId.CompareTo(componentId);
            if (order < 0)
            {
                low = middle + 1;
                continue;
            }
            if (order > 0)
            {
                high = middle - 1;
                continue;
            }
            if (!requireWrite || capability.CanWrite)
                return capability;
            break;
        }

        // Preserve the Job layer's precise safety diagnostic for undeclared access. This path is
        // exceptional; declared propagation accesses use the precompiled capability above.
        JobResourceAccess required = requireWrite
            ? ComponentJobAccess<T>.Write(_world)
            : ComponentJobAccess<T>.Read(_world);
        JobSystem.RequireCurrentAccess(required);
        return new HierarchyPropagationComponentCapability(
            componentId,
            required,
            requireWrite);
    }

    private bool IsAncestor(Entity target, Entity current)
    {
        Entity ancestor = GetParent(current);
        while (ancestor != Entity.Null)
        {
            if (ancestor == target)
                return true;
            ancestor = GetParent(ancestor);
        }
        return false;
    }

    private long CapturedAddress(Entity entity)
    {
        ReadOnlySpan<HierarchyPropagationEntityAddress> entityAddresses =
            _entityAddresses.Span;
        int low = 0;
        int high = entityAddresses.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            HierarchyPropagationEntityAddress candidate = entityAddresses[middle];
            int order = candidate.Entity.Index.CompareTo(entity.Index);
            if (order == 0)
                order = candidate.Entity.Generation.CompareTo(entity.Generation);
            if (order < 0)
            {
                low = middle + 1;
                continue;
            }
            if (order > 0)
            {
                high = middle - 1;
                continue;
            }
            return candidate.Address;
        }

        throw new InvalidOperationException(
            $"Hierarchy propagation did not capture a stable row address for ancestor {entity}.");
    }

    private JobResourceAccess ComponentRangeAccess(
        int componentId,
        long address,
        bool write)
    {
        if (address < 0)
            throw new InvalidOperationException("A stable component row address cannot be negative.");
        var key = new WorldStorageResourceKey(WorldStorageKind.Table, componentId);
        return write
            ? WorldStorageJobResources.Write(_world, key, address, length: 1)
            : WorldStorageJobResources.Read(_world, key, address, length: 1);
    }
}

internal sealed class HierarchyPropagationExecutionVersion
{
    private readonly Lock _gate = new();
    private bool _initialized;
    private uint _version;

    internal uint Get(World world)
    {
        if (Volatile.Read(ref _initialized))
            return _version;

        lock (_gate)
        {
            if (!_initialized)
            {
                _version = world.AcquireAdmittedSystemVersion();
                Volatile.Write(ref _initialized, true);
            }
            return _version;
        }
    }
}

internal readonly struct HierarchyPropagationComponentCapability
{
    internal HierarchyPropagationComponentCapability(
        int componentId,
        JobResourceAccess access,
        bool canWrite)
    {
        ComponentId = componentId;
        Access = access;
        CanWrite = canWrite;
    }

    internal int ComponentId { get; }

    internal JobResourceAccess Access { get; }

    internal bool CanWrite { get; }
}

internal readonly struct HierarchyPropagationEntityAddress
{
    internal HierarchyPropagationEntityAddress(Entity entity, long address)
    {
        Entity = entity;
        Address = address;
    }

    internal Entity Entity { get; }

    internal long Address { get; }
}
