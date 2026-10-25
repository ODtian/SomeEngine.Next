using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS;

/// <summary>
/// Storage admission mode shared with the scheduling integration. ECS references only the Job
/// execution-scope sentinel directly; resource mapping remains in SomeEngine.ECS.Systems.
/// </summary>
internal enum WorldTopologyAccess : byte
{
    Read = 0,
    Write = 1,
}

internal enum WorldStorageKind : byte
{
    Table = 0,
    Buffer = 1,
    Sparse = 2,
    Shared = 3,
    Topology = 4,
}

internal enum WorldStorageAccess : byte
{
    None = 0,
    Read = 1,
    Write = 2,
}

/// <summary>
/// A query-definition-time normalized logical storage borrow. Buffer header and inline backing
/// columns intentionally collapse to the same header identity.
/// </summary>
internal readonly record struct WorldJobStorageAccess(
    WorldStorageKind Kind,
    int ComponentId,
    WorldStorageAccess Access);

/// <summary>
/// Describes the storage borrowed by one runtime-owned World callback. The Systems integration
/// maps logical storage component ids to Job resource identities; ECS itself only uses Job's
/// execution-scope sentinel to make an absent integration fail closed.
/// </summary>
internal readonly struct WorldJobAdmissionRequest
{
    private WorldJobAdmissionRequest(
        WorldTopologyAccess topology,
        WorldStorageKind storageKind,
        int storageComponentId,
        WorldStorageAccess storageAccess,
        ReadOnlyMemory<WorldJobStorageAccess> queryStorageAccesses,
        bool canWrite,
        bool requiresUnboundMutationGate,
        bool bumpsTopologyRevision)
    {
        Topology = topology;
        StorageKind = storageKind;
        StorageComponentId = storageComponentId;
        StorageAccess = storageAccess;
        QueryStorageAccesses = queryStorageAccesses;
        CanWrite = canWrite;
        RequiresUnboundMutationGate = requiresUnboundMutationGate;
        BumpsTopologyRevision = bumpsTopologyRevision;
    }

    internal WorldTopologyAccess Topology { get; }

    internal WorldStorageKind StorageKind { get; }

    internal int StorageComponentId { get; }

    internal WorldStorageAccess StorageAccess { get; }

    internal ReadOnlyMemory<WorldJobStorageAccess> QueryStorageAccesses { get; }

    internal bool CanWrite { get; }

    /// <summary>
    /// True when an unbound synchronous caller mutates state owned by the current structural
    /// root. This is deliberately independent of <see cref="CanWrite"/>: root-control mutations
    /// such as allocating a clock version or registering a query remain topology-read operations
    /// for the bound Job coordinator and must not be treated as component/hook writes, but they
    /// still cannot overlap an unbound structural candidate clone and publication.
    /// </summary>
    internal bool RequiresUnboundMutationGate { get; }

    /// <summary>
    /// True when entering this owner publishes a topology-mutation boundary. Control-plane
    /// operations may require topology-exclusive ownership without changing the structure.
    /// </summary>
    internal bool BumpsTopologyRevision { get; }

    internal static WorldJobAdmissionRequest ForTopology(WorldTopologyAccess access) =>
        new(
            access,
            WorldStorageKind.Buffer,
            storageComponentId: -1,
            storageAccess: WorldStorageAccess.None,
            queryStorageAccesses: default,
            canWrite: access == WorldTopologyAccess.Write,
            requiresUnboundMutationGate: access == WorldTopologyAccess.Write,
            bumpsTopologyRevision: access == WorldTopologyAccess.Write);

    internal static WorldJobAdmissionRequest ForTopologyControlPlane() =>
        new(
            WorldTopologyAccess.Write,
            WorldStorageKind.Buffer,
            storageComponentId: -1,
            storageAccess: WorldStorageAccess.None,
            queryStorageAccesses: default,
            canWrite: false,
            requiresUnboundMutationGate: true,
            bumpsTopologyRevision: false);

    /// <summary>
    /// A root-local control mutation which remains a topology read for bound Job resource
    /// admission. In unbound mode it serializes with structural root cloning/publication so the
    /// candidate cannot overwrite a clock or query-registry update made against its source.
    /// </summary>
    internal static WorldJobAdmissionRequest ForRootControlMutation() =>
        new(
            WorldTopologyAccess.Read,
            WorldStorageKind.Buffer,
            storageComponentId: -1,
            storageAccess: WorldStorageAccess.None,
            queryStorageAccesses: default,
            canWrite: false,
            requiresUnboundMutationGate: true,
            bumpsTopologyRevision: false);

    internal static WorldJobAdmissionRequest ForStorage(
        WorldStorageKind kind,
        int componentId,
        WorldStorageAccess access) =>
        new(
            WorldTopologyAccess.Read,
            kind,
            componentId,
            access,
            queryStorageAccesses: default,
            canWrite: access == WorldStorageAccess.Write,
            requiresUnboundMutationGate: access == WorldStorageAccess.Write,
            bumpsTopologyRevision: false);

    internal static WorldJobAdmissionRequest ForQuery(
        WorldTopologyAccess topology,
        ReadOnlyMemory<WorldJobStorageAccess> accesses,
        bool canWrite) =>
        new(
            topology,
            WorldStorageKind.Buffer,
            storageComponentId: -1,
            storageAccess: WorldStorageAccess.None,
            queryStorageAccesses: accesses,
            canWrite: canWrite,
            requiresUnboundMutationGate: canWrite,
            bumpsTopologyRevision: topology == WorldTopologyAccess.Write);

    internal static WorldJobAdmissionRequest ForReadQuery(ReadOnlyMemory<WorldJobStorageAccess> accesses) =>
        new(WorldTopologyAccess.Read, WorldStorageKind.Buffer, -1, WorldStorageAccess.None, accesses,
            canWrite: false, requiresUnboundMutationGate: true, bumpsTopologyRevision: false);
}

internal readonly struct WorldJobAdmissionScope : IDisposable
{
    private readonly World? _world;
    private readonly WorldExecution? _admission;
    private readonly WorldJobAdmissionRequest _request;
    private readonly bool _unboundAdmission;
    private readonly bool _unboundMutationGate;

    internal WorldJobAdmissionScope(
        World world,
        WorldExecution admission,
        in WorldJobAdmissionRequest request)
    {
        _world = world;
        _admission = admission;
        _request = request;
        _unboundAdmission = false;
        _unboundMutationGate = false;
    }

    internal WorldJobAdmissionScope(World world, bool unboundMutationGate)
    {
        _world = world;
        _admission = null;
        _request = default;
        _unboundAdmission = true;
        _unboundMutationGate = unboundMutationGate;
    }

    public void Dispose()
    {
        if (_admission is not null)
            _admission.Exit(_world!, in _request);
        else if (_unboundAdmission)
            _world!.ExitUnboundJobAdmission(_unboundMutationGate);
    }
}

internal enum RestrictedWorldApiContext : byte
{
    HierarchyPropagation = 1,
}

/// <summary>
/// Prevents an unmanaged callback from escaping its narrowed context through a static/GCHandle
/// reference to World. This is thread-scoped because Job callbacks are synchronous and may run in
/// parallel on different workers.
/// </summary>
internal struct RestrictedWorldApiScope : IDisposable
{
    private readonly int _ownerThreadId;
    private readonly RestrictedWorldApiContext _context;
    private bool _disposed;

    internal RestrictedWorldApiScope(
        int ownerThreadId,
        RestrictedWorldApiContext context)
    {
        _ownerThreadId = ownerThreadId;
        _context = context;
        _disposed = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        World.ExitRestrictedWorldApi(_ownerThreadId, _context);
        _disposed = true;
    }
}

/// <summary>
/// Grants one runtime-owned producer access to exactly one producer-private command segment for
/// the synchronous duration of a Job callback. The segment itself owns a private gate; this scope
/// never authorizes <see cref="World.Commands"/> or another CommandBuffer instance.
/// </summary>
internal struct JobCommandProducerScope : IDisposable
{
    private readonly int _ownerThreadId;
    private readonly CommandBuffer? _buffer;
    private bool _disposed;

    internal JobCommandProducerScope(int ownerThreadId, CommandBuffer buffer)
    {
        _ownerThreadId = ownerThreadId;
        _buffer = buffer;
        _disposed = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        World.ExitJobCommandProducer(_ownerThreadId, _buffer!);
        _disposed = true;
    }
}

public partial class World
{
    [ThreadStatic]
    private static RestrictedWorldApiContext s_restrictedWorldApiContext;

    [ThreadStatic]
    private static int s_restrictedWorldApiDepth;

    [ThreadStatic]
    private static Dictionary<World, int>? s_unboundJobAdmissions;

    [ThreadStatic]
    private static CommandBuffer? s_jobCommandProducerBuffer;

    private readonly object _unboundAccessGate = new();
    private readonly object _unboundMutationGate = new();
    private readonly WorldExecution? _execution;

    internal WorldExecution? Execution => _execution;
    private int _unboundJobAdmissionCount;

    internal static RestrictedWorldApiScope EnterRestrictedWorldApi(
        RestrictedWorldApiContext context)
    {
        if (context == default)
            throw new ArgumentOutOfRangeException(nameof(context));
        if (s_restrictedWorldApiDepth != 0)
        {
            throw new InvalidOperationException(
                "Nested restricted World callback scopes are not supported.");
        }

        int threadId = Environment.CurrentManagedThreadId;
        s_restrictedWorldApiContext = context;
        s_restrictedWorldApiDepth = 1;
        return new RestrictedWorldApiScope(threadId, context);
    }

    internal static void ThrowIfRestrictedWorldApi()
    {
        if (s_restrictedWorldApiDepth == 0)
            return;

        string callback = s_restrictedWorldApiContext switch
        {
            RestrictedWorldApiContext.HierarchyPropagation =>
                "an IHierarchyPropagationJob callback",
            _ => "a restricted ECS callback",
        };
        throw new InvalidOperationException(
            $"Ordinary World and CommandBuffer APIs are forbidden inside {callback}. " +
            "Use HierarchyPropagationContext<TDomain> for all ECS access.");
    }

    internal void ThrowIfJobCommandBufferAccess(CommandBuffer? producerBuffer = null)
    {
        ThrowIfRestrictedWorldApi();
        if (producerBuffer is not null &&
            ReferenceEquals(s_jobCommandProducerBuffer, producerBuffer))
        {
            return;
        }
        if (producerBuffer?.IsJobProducerOwned == true)
        {
            throw new InvalidOperationException(
                "A producer-private CommandBuffer segment may only be used by its active Job " +
                "producer callback.");
        }
        if (_hooks.IsExecutingOnCurrentThread)
        {
            throw new InvalidOperationException(
                "Raw CommandBuffer APIs are forbidden inside an immediate component hook. " +
                "Record next-wave work through the hook's DeferredWorld.Commands() writer.");
        }
        ResolveJobAdmissionForCurrentScope()?.ValidateCommandBufferAccess(this);
    }

    internal void ThrowIfJobCommandProducerControlPlane()
    {
        ThrowIfRestrictedWorldApi();
        CommandBuffer? activeProducer = s_jobCommandProducerBuffer;
        if (activeProducer is null)
            return;

        string worldScope = ReferenceEquals(activeProducer.OwnerWorld, this)
            ? "its owning World"
            : "another World";
        throw new InvalidOperationException(
            "JobCommandBuffer scheduling, playback, and disposal are forbidden inside an active " +
            $"Job command producer callback, including buffers for {worldScope}. The callback " +
            "may only record through its JobCommandWriter; return to the batch owner before " +
            "controlling producer or playback lifetimes.");
    }

    internal JobCommandProducerScope EnterJobCommandProducer(CommandBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!JobExecutionContext.IsActive)
        {
            throw new InvalidOperationException(
                "A Job command producer scope may only be entered by a running Job callback.");
        }
        if (!buffer.IsJobProducerOwned || !ReferenceEquals(buffer.OwnerWorld, this))
        {
            throw new InvalidOperationException(
                "The command segment does not belong to this World or is not producer-private.");
        }
        if (s_jobCommandProducerBuffer is not null)
        {
            throw new InvalidOperationException(
                "Nested Job command producer scopes are not supported.");
        }

        s_jobCommandProducerBuffer = buffer;
        return new JobCommandProducerScope(Environment.CurrentManagedThreadId, buffer);
    }

    internal static void ExitJobCommandProducer(int ownerThreadId, CommandBuffer buffer)
    {
        if (ownerThreadId != Environment.CurrentManagedThreadId ||
            !ReferenceEquals(s_jobCommandProducerBuffer, buffer))
        {
            throw new InvalidOperationException(
                "Job command producer scope is unbalanced or disposed on another thread.");
        }

        s_jobCommandProducerBuffer = null;
    }

    internal void RequireJobSharedRead<T>(string operation)
        where T : struct, ISharedComponent
    {
        WorldExecution? admission = ResolveJobAdmissionForCurrentScope();
        if (admission is null || !admission.HasCurrentJobScope)
            return;

        JobStorageTypeMetadata<T>.RequireAliasFree(operation);

        // Shared-value reads are authorized independently from topology reads. Validate the
        // precise type capability only while a Job callback is active; synchronous callers are
        // already protected by their topology owner and do not create a redundant shared-value
        // frontier. The short nested admission cannot let a ref escape because shared values are
        // returned by value.
        using WorldJobAdmissionScope sharedAdmission = EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Shared,
                ComponentMetadata<T>.Id,
                WorldStorageAccess.Read));
    }

    internal void ValidateHookCommandBufferRecordAccessUnderGate(
        CommandBuffer buffer,
        HookCommandToken token)
    {
        ThrowIfRestrictedWorldApi();
        ArgumentNullException.ThrowIfNull(buffer);
        _hooks.ValidateCommandToken(token);
        _commands.RequireCurrentHookBufferUnderGate(buffer, token);
    }

    internal static void ExitRestrictedWorldApi(
        int ownerThreadId,
        RestrictedWorldApiContext context)
    {
        if (s_restrictedWorldApiDepth != 1 ||
            s_restrictedWorldApiContext != context ||
            Environment.CurrentManagedThreadId != ownerThreadId)
        {
            throw new InvalidOperationException(
                "Restricted World callback scope is unbalanced or disposed on another thread.");
        }

        s_restrictedWorldApiDepth = 0;
        s_restrictedWorldApiContext = default;
    }

    private WorldExecution? ResolveJobAdmissionForCurrentScope()
    {
        if (_execution is not null)
            return _execution;
        if (JobExecutionContext.IsActive)
            throw new InvalidOperationException(
                "This World was created for synchronous use. Create it with WorldJobs to schedule ECS work.");
        return null;
    }

    private WorldJobAdmissionScope EnterUnboundJobAdmission(bool mutationGateHeld)
    {
        if (mutationGateHeld && !Monitor.IsEntered(_unboundMutationGate))
        {
            throw new InvalidOperationException(
                "Unbound World mutation admission must own its mutation gate.");
        }

        Dictionary<World, int> admissions =
            s_unboundJobAdmissions ??= new Dictionary<World, int>();
        admissions.TryGetValue(this, out int depth);
        admissions[this] = checked(depth + 1);
        _unboundJobAdmissionCount = checked(_unboundJobAdmissionCount + 1);
        return new WorldJobAdmissionScope(this, mutationGateHeld);
    }

    internal void ExitUnboundJobAdmission(bool mutationGateHeld)
    {
        try
        {
            lock (_unboundAccessGate)
            {
                Dictionary<World, int>? admissions = s_unboundJobAdmissions;
                if (admissions is null ||
                    !admissions.TryGetValue(this, out int depth) ||
                    depth <= 0 ||
                    _unboundJobAdmissionCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Unbound World Job admission scope is unbalanced or disposed on another thread.");
                }

                if (depth == 1)
                    admissions.Remove(this);
                else
                    admissions[this] = depth - 1;

                _unboundJobAdmissionCount--;
                Monitor.PulseAll(_unboundAccessGate);
            }
        }
        finally
        {
            // A copied scope disposed by the wrong thread must fail closed without releasing the
            // actual owner's monitor recursion. The owner can still dispose its copy correctly.
            if (mutationGateHeld && Monitor.IsEntered(_unboundMutationGate))
                Monitor.Exit(_unboundMutationGate);
        }
    }

    internal WorldJobAdmissionScope EnterJobTopologyRead()
    {
        return EnterJobTopology(WorldTopologyAccess.Read);
    }

    internal WorldJobAdmissionScope EnterJobTopologyWrite()
    {
        return EnterJobTopology(WorldTopologyAccess.Write);
    }

    internal WorldJobAdmissionScope EnterJobQuery(
        QueryHandle query,
        out bool relationshipWrite)
    {
        QueryDefinition definition = _queries.Get(query).Definition;
        WorldTopologyAccess topology = definition.HasRelationshipWrite
            ? WorldTopologyAccess.Write
            : WorldTopologyAccess.Read;
        relationshipWrite = definition.HasRelationshipWrite;

        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForQuery(
                topology,
                definition.JobStorageAccesses,
                definition.CanWrite));
    }

    internal WorldJobAdmissionScope EnterJobBuffer<T>(WorldStorageAccess access)
        where T : struct, IBufferElement
    {
        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Buffer,
                BufferComponents.Header<T>(),
                access));
    }

    internal WorldJobAdmissionScope EnterJobComponent<T>(WorldStorageAccess access)
        where T : struct, IComponent
    {
        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Table,
                ComponentMetadata<T>.Id,
                access));
    }

    internal WorldJobAdmissionScope EnterJobComponent(
        int componentId,
        WorldStorageAccess access)
    {
        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Table,
                componentId,
                access));
    }

    internal WorldJobAdmissionScope EnterJobSparse<T>(WorldStorageAccess access)
        where T : struct
    {
        return EnterJobAdmission(
            WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Sparse,
                ComponentMetadata<T>.Id,
                access));
    }

    private WorldJobAdmissionScope EnterJobTopology(WorldTopologyAccess access)
    {
        return EnterJobAdmission(WorldJobAdmissionRequest.ForTopology(access));
    }

    private WorldJobAdmissionScope EnterRootControlMutation()
    {
        return EnterJobAdmission(WorldJobAdmissionRequest.ForRootControlMutation());
    }

    private WorldJobAdmissionScope EnterJobAdmission(in WorldJobAdmissionRequest request)
    {
        return EnterJobAdmission(
            in request,
            allowClosing: false,
            allowReadSnapshotNesting: false);
    }

    private WorldJobAdmissionScope EnterJobAdmission(
        in WorldJobAdmissionRequest request,
        bool allowClosing,
        bool allowReadSnapshotNesting = false)
    {
        if (!allowClosing)
            ThrowIfUnavailable();
        ThrowIfRestrictedWorldApi();
        if (!allowReadSnapshotNesting)
            ThrowIfReadSnapshotMutation(in request);
        bool topologyWrite = request.Topology == WorldTopologyAccess.Write;
        bool hookSafeDirectStorageWrite =
            request.StorageAccess == WorldStorageAccess.Write &&
            request.StorageKind is WorldStorageKind.Buffer or WorldStorageKind.Sparse;
        bool admittedJobStorageWrite =
            JobExecutionContext.IsActive && request.CanWrite && !topologyWrite;
        _hooks.ThrowIfReentrantWorldMutation(
            (request.CanWrite || topologyWrite) &&
            !hookSafeDirectStorageWrite &&
            !admittedJobStorageWrite);
        _bundles.ThrowIfReentrantWorldMutation(request.CanWrite);
        WorldExecution? admission = ResolveJobAdmissionForCurrentScope();
        if (admission is null)
        {
            bool mutationGateHeld = request.RequiresUnboundMutationGate;
            if (mutationGateHeld)
                Monitor.Enter(_unboundMutationGate);
            WorldJobAdmissionScope scope;
            try
            {
                lock (_unboundAccessGate)
                    scope = EnterUnboundJobAdmission(mutationGateHeld);
            }
            catch
            {
                if (mutationGateHeld)
                    Monitor.Exit(_unboundMutationGate);
                throw;
            }
            try
            {
                ThrowIfUnavailable();
                if (request.BumpsTopologyRevision)
                    BumpTopologyRevision();
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        admission.Enter(this, in request);
        try
        {
            ThrowIfUnavailable();
            if (request.BumpsTopologyRevision)
                BumpTopologyRevision();
            return new WorldJobAdmissionScope(this, admission, in request);
        }
        catch
        {
            admission.Exit(this, in request);
            throw;
        }
    }

}
