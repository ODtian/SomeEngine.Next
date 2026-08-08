using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Sparse;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Serialization;

/// <summary>
/// Root-pinned view of one admitted World image used by streaming serialization.
/// </summary>
/// <remarks>
/// Capture validation runs under topology-exclusive admission. Before caller output, the source
/// root is retained by this thread, a semantically identical copy-on-write successor is published,
/// and topology admission is released. Codecs read the retained root's final backing directly;
/// concurrent mutations detach only successor pages/shards, while World disposal still waits for
/// this explicit serialization lifetime. No encoded payload or second value graph is staged.
/// Whole-World and checkpoint callers additionally reject present registered values containing
/// managed references because external aliases are outside World ownership. Entity-, query-, and
/// delta-scoped callers retain their narrower documented contracts.
/// </remarks>
internal sealed class AdmittedWorldWrite : IDisposable
{
    private readonly World _world;
    private readonly WorldStructureRoot _root;
    private WorldJobAdmissionScope _admission;
    private World.SerializationWriteLifetimeScope _lifetime;
    private readonly int _ownerThreadId;
    private int _captureCompleted;
    private int _disposed;

    private AdmittedWorldWrite(
        World world,
        WorldStructureRoot root,
        uint tick,
        WorldJobAdmissionScope admission,
        World.SerializationWriteLifetimeScope lifetime)
    {
        _world = world;
        _root = root;
        _admission = admission;
        _lifetime = lifetime;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        CurrentTick = tick;
    }

    internal ReadOnlySpan<Archetype> Archetypes => _root.Tables.All;

    /// <summary>
    /// Number of identity slots in the pinned root. Slot metadata is read lazily; capture does not
    /// allocate an <c>EntitySlotSnapshot[SlotCount]</c> array.
    /// </summary>
    internal int SlotCount => _root.Entities.Store.Count;

    internal int LiveEntityCount => _root.Entities.Store.AliveCount;

    internal uint CurrentTick { get; }

    /// <summary>
    /// Admits and validates the current root, pins it to this thread, publishes a same-image
    /// successor, and releases topology admission before returning. The optional validation
    /// callback runs before the ownership handoff and caller output in a restricted World scope.
    /// </summary>
    internal static AdmittedWorldWrite Enter(
        World world,
        Action<AdmittedWorldWrite>? validateBeforeOutput = null)
    {
        AdmittedWorldWrite? admitted = null;
        try
        {
            admitted = BeginCapture(world);
            validateBeforeOutput?.Invoke(admitted);
            admitted.CompleteCapture();
            return admitted;
        }
        catch (Exception captureFailure)
        {
            Exception? releaseFailure = null;
            try
            {
                admitted?.Dispose();
            }
            catch (Exception exception)
            {
                releaseFailure = exception;
            }
            if (releaseFailure is not null)
            {
                throw new AggregateException(
                    "World serialization capture and scope release both failed.",
                    captureFailure,
                    releaseFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(captureFailure)
                .Throw();
            throw;
        }
    }

    /// <summary>
    /// Begins a capture while retaining topology admission. This two-step form exists for callers
    /// such as <c>WriteEntities(ReadOnlySpan&lt;Entity&gt;)</c> whose stack-only inputs cannot be
    /// captured by the ordinary validation callback. The caller must validate and then call
    /// <see cref="CompleteCapture"/> before writing output.
    /// </summary>
    internal static AdmittedWorldWrite BeginCapture(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.ThrowIfSerializationWriteInsideStructuralCandidate();
        World.SerializationWriteLifetimeScope lifetime =
            world.EnterSerializationWriteLifetime();
        WorldJobAdmissionScope admission = default;
        try
        {
            admission = world.EnterSerializationWriteControlPlane();
            world.ThrowIfSerializationWriteInsideStructuralCandidate();
            WorldStructureRoot root = world.PublishedStructureRoot;
            var admitted = new AdmittedWorldWrite(
                world,
                root,
                root.Clock.Tick,
                admission,
                lifetime);
            admission = default;
            lifetime = default;
            return admitted;
        }
        catch (Exception captureFailure)
        {
            Exception? releaseFailure = ReleaseCaptureScopes(
                ref admission,
                ref lifetime);
            if (releaseFailure is not null)
            {
                throw new AggregateException(
                    "World serialization admission and capture setup both failed.",
                    captureFailure,
                    releaseFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(captureFailure)
                .Throw();
            throw;
        }
    }

    /// <summary>
    /// Completes a validated capture by pinning the source, publishing its exact COW successor,
    /// and releasing topology admission before caller-controlled output begins.
    /// </summary>
    internal void CompleteCapture()
    {
        if (_ownerThreadId != Environment.CurrentManagedThreadId ||
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _captureCompleted) != 0)
        {
            throw new InvalidOperationException(
                "World serialization capture must complete exactly once on its owning thread.");
        }

        try
        {
            WorldStructureRoot successor = _root.CloneSerializationSuccessor(
                _world,
                _world.HookStore);
            _world.PublishSerializationSuccessor(_root, successor);

            Exception? admissionFailure = ReleaseAdmission(ref _admission);
            if (admissionFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(admissionFailure)
                    .Throw();
            }

            Volatile.Write(ref _captureCompleted, 1);
        }
        catch (Exception captureFailure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(captureFailure)
                .Throw();
            throw;
        }
    }

    internal EntitySlotSnapshot GetSlot(int slotOffset)
    {
        int slotCount = SlotCount;
        if ((uint)slotOffset >= (uint)slotCount)
            throw new ArgumentOutOfRangeException(nameof(slotOffset));

        int index = checked(slotOffset + 1);
        EntityStore store = _root.Entities.Store;
        return new EntitySlotSnapshot(
            index,
            store.GetGeneration(index),
            store.IsAliveIndex(index));
    }

    internal EntityRecord ReadRecord(Entity entity) =>
        _root.Entities.Store.GetRecordReadOnly(entity);

    internal bool IsAlive(Entity entity) => _root.Entities.Alive(entity);

    internal bool HasValue<T>(Entity entity)
        where T : struct => _root.Components.Has<T>(entity);

    internal bool HasShared<T>(Entity entity)
        where T : struct, ISharedComponent => _root.Shared.Has<T>(entity);

    internal bool HasSparse<T>(Entity entity)
        where T : struct => _root.Sparse.Has<T>(entity);

    internal bool HasBuffer<T>(Entity entity)
        where T : struct, IBufferElement => _root.Buffers.Has<T>(entity);

    internal ref readonly T ReadValue<T>(Entity entity)
        where T : struct, IComponent => ref _root.Components.ReadRef<T>(entity);

    internal ref readonly T ReadShared<T>(Entity entity)
        where T : struct, ISharedComponent => ref _root.Shared.GetRef<T>(entity);

    internal ref readonly T ReadSparse<T>(Entity entity)
        where T : struct, ISparseComponent => ref _root.Sparse.ReadRef<T>(entity);

    internal BufferView<T> BorrowBuffer<T>(Entity entity)
        where T : struct, IBufferElement => _root.Buffers.BorrowRead<T>(entity);

    internal bool IsValueEnabled(Entity entity, int componentId) =>
        _root.Components.IsEnabled(entity, componentId);

    internal bool TrySparseSet<T>(out SparseSet<T> sparseSet)
        where T : struct => _root.Sparse.TrySet(out sparseSet);

    internal HierarchyTopologyWriteAccess<TDomain> OpenHierarchyTopology<TDomain>()
        where TDomain : IHierarchyDomain =>
        new(_root.Hierarchy.Domain<TDomain>());

    internal RelationTopologyWriteAccess<T> OpenRelationTopology<T>(bool validate)
        where T : struct, IComponent =>
        new(_root, validate);

    internal void RecordRelationTopologyWrite<T>(int edgeVisits, int orderedShardVisits)
        where T : struct, IComponent =>
        _world.RecordRelationTopologyWrite<T>(edgeVisits, orderedShardVisits);

    internal void ExecuteQuery(QueryHandle query, QueryExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        QueryState state = _root.Queries.Get(query).State;
        _root.Iteration.BeginQueryBorrow();
        try
        {
            execution(new QueryCursor(_world, state, CurrentTick, CurrentTick));
        }
        finally
        {
            _root.Iteration.EndQueryBorrow();
        }
    }

    public void Dispose()
    {
        if (_ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "World serialization root must be released by its owning thread.");
        }
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? releaseFailure = ReleaseCaptureScopes(
            ref _admission,
            ref _lifetime);
        if (releaseFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(releaseFailure)
                .Throw();
        }
    }

    private static Exception? ReleaseCaptureScopes(
        ref WorldJobAdmissionScope admission,
        ref World.SerializationWriteLifetimeScope lifetime)
    {
        List<Exception>? failures = null;
        Exception? admissionFailure = ReleaseAdmission(ref admission);
        if (admissionFailure is not null)
            (failures ??= new List<Exception>(2)).Add(admissionFailure);

        try
        {
            lifetime.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= new List<Exception>(2)).Add(exception);
        }
        lifetime = default;

        return CombineReleaseFailures(
            failures,
            "World serialization admission or lifetime release failed.");
    }

    private static Exception? ReleaseAdmission(ref WorldJobAdmissionScope admission)
    {
        try
        {
            admission.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            admission = default;
        }
    }

    private static Exception? CombineReleaseFailures(
        List<Exception>? failures,
        string aggregateMessage)
    {
        if (failures is null || failures.Count == 0)
            return null;
        if (failures.Count == 1)
            return failures[0];
        return new AggregateException(aggregateMessage, failures);
    }
}

/// <summary>
/// Per-snapshot lookup of sparse serialization items. It stores only runtime identities, never
/// captured component values or encoded payloads, and is built by walking each sparse dense set
/// once rather than probing every registered type for every entity.
/// </summary>
internal sealed class SparseSerializationPresence
{
    private readonly Dictionary<Entity, List<SerializationTypeRuntime>> _items = new();
    private readonly HashSet<SerializationTypeRuntime> _presentRuntimes = new();
    private readonly int _maximumMemberships;
    private int _membershipCount;

    internal SparseSerializationPresence(int maximumMemberships)
    {
        if (maximumMemberships < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMemberships));
        _maximumMemberships = maximumMemberships;
    }

    internal int EntityCount => _items.Count;

    internal int MembershipCount => _membershipCount;

    internal void Add(
        SerializationTypeRuntime runtime,
        ReadOnlySpan<Entity> entities)
    {
        if (entities.Length == 0)
            return;
        if (entities.Length > _maximumMemberships - _membershipCount)
        {
            throw new InvalidOperationException(
                $"World serialization sparse membership count exceeds the configured limit " +
                $"of {_maximumMemberships}.");
        }

        _presentRuntimes.Add(runtime);
        _membershipCount += entities.Length;
        for (int i = 0; i < entities.Length; i++)
        {
            Entity entity = entities[i];
            if (!_items.TryGetValue(entity, out List<SerializationTypeRuntime>? runtimes))
            {
                runtimes = new List<SerializationTypeRuntime>(1);
                _items.Add(entity, runtimes);
            }

            runtimes.Add(runtime);
        }
    }

    internal ReadOnlySpan<SerializationTypeRuntime> For(Entity entity) =>
        _items.TryGetValue(entity, out List<SerializationTypeRuntime>? runtimes)
            ? CollectionsMarshal.AsSpan(runtimes)
            : ReadOnlySpan<SerializationTypeRuntime>.Empty;

    internal void AddPresentRuntimesTo(HashSet<SerializationTypeRuntime> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.UnionWith(_presentRuntimes);
    }

    internal void Sort()
    {
        foreach (List<SerializationTypeRuntime> runtimes in _items.Values)
        {
            runtimes.Sort(static (left, right) =>
                SerializationRegistry.CompareTypeKeys(
                    left.Entry.TypeKey,
                    right.Entry.TypeKey));
        }
    }
}

/// <summary>
/// Archetype-oriented write metadata for one admission-stable image. Runtime discovery is paid once
/// per archetype and sparse dense set; entity encoding subsequently touches only items that are
/// actually present.
/// </summary>
/// <remarks>
/// Slot identities are read lazily and consume O(1) plan memory. The plan itself uses
/// O(A + T + R + S + M) references: A archetype entries, T table-runtime associations across
/// live archetypes, R manifest runtimes, S distinct sparse-bearing entities, and M sparse
/// memberships. M is necessarily retained for the v4 entity-framed stable-key merge and is
/// bounded for <see cref="WorldSerializer.WriteWorld"/> by
/// <see cref="SerializeOptions.MaximumSparseMemberships"/>. Encoding borrows each final component
/// or buffer value directly; its bytes write to the destination exactly once and are followed by a
/// length footer, so non-seekable output does not retain a complete encoded item backing.
/// </remarks>
internal sealed class WorldWritePlan
{
    private readonly Dictionary<Archetype, SerializationTypeRuntime[]> _archetypeRuntimes;
    private readonly SparseSerializationPresence _sparsePresence;
    private readonly SerializationTypeRuntime[] _manifest;

    private WorldWritePlan(
        Dictionary<Archetype, SerializationTypeRuntime[]> archetypeRuntimes,
        SparseSerializationPresence sparsePresence,
        SerializationTypeRuntime[] manifest)
    {
        _archetypeRuntimes = archetypeRuntimes;
        _sparsePresence = sparsePresence;
        _manifest = manifest;
    }

    internal ReadOnlySpan<SerializationTypeRuntime> Manifest => _manifest;

    internal int SparseEntityCount => _sparsePresence.EntityCount;

    internal int SparseMembershipCount => _sparsePresence.MembershipCount;

    internal static WorldWritePlan Build(
        AdmittedWorldWrite admitted,
        SerializationRegistry registry,
        int maximumSparseMemberships = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentNullException.ThrowIfNull(registry);

        var runtimeByComponentId = new Dictionary<int, SerializationTypeRuntime>();
        var sparsePresence = new SparseSerializationPresence(maximumSparseMemberships);
        ReadOnlySpan<SerializationTypeRuntime> registered = registry.RuntimeTypes;
        for (int i = 0; i < registered.Length; i++)
        {
            SerializationTypeRuntime runtime = registered[i];
            if (runtime.Entry.Kind != SerializationValueKind.Sparse)
                runtimeByComponentId.TryAdd(runtime.Entry.RuntimeComponentId, runtime);
        }

        // Admission keeps the published root stable while sparse runtimes expose their dense
        // membership. No encoded values are captured here and no stream callback runs yet.
        for (int i = 0; i < registered.Length; i++)
            registered[i].CollectSparsePresence(admitted, sparsePresence);
        sparsePresence.Sort();

        var manifestSet = new HashSet<SerializationTypeRuntime>();
        sparsePresence.AddPresentRuntimesTo(manifestSet);
        var byArchetype = new Dictionary<Archetype, SerializationTypeRuntime[]>();
        foreach (Archetype archetype in admitted.Archetypes)
        {
            bool hasLiveRows = false;
            ReadOnlySpan<Chunk> chunks = archetype.Chunks;
            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                if (chunks[chunkIndex].Count != 0)
                {
                    hasLiveRows = true;
                    break;
                }
            }

            if (!hasLiveRows)
            {
                byArchetype.Add(archetype, Array.Empty<SerializationTypeRuntime>());
                continue;
            }

            var runtimes = new List<SerializationTypeRuntime>();
            for (int componentIndex = 0;
                 componentIndex < archetype.ComponentIds.Length;
                 componentIndex++)
            {
                if (runtimeByComponentId.TryGetValue(
                        archetype.ComponentIds[componentIndex],
                        out SerializationTypeRuntime? runtime))
                {
                    runtimes.Add(runtime);
                    manifestSet.Add(runtime);
                }
            }

            runtimes.Sort(static (left, right) =>
                SerializationRegistry.CompareTypeKeys(
                    left.Entry.TypeKey,
                    right.Entry.TypeKey));
            byArchetype.Add(archetype, runtimes.ToArray());
        }

        SerializationTypeRuntime[] manifest = manifestSet.ToArray();
        Array.Sort(manifest, static (left, right) =>
            SerializationRegistry.CompareTypeKeys(
                left.Entry.TypeKey,
                right.Entry.TypeKey));
        return new WorldWritePlan(byArchetype, sparsePresence, manifest);
    }

    internal ReadOnlySpan<SerializationTypeRuntime> TableItems(Archetype archetype) =>
        _archetypeRuntimes[archetype];

    internal ReadOnlySpan<SerializationTypeRuntime> SparseItems(Entity entity) =>
        _sparsePresence.For(entity);
}
