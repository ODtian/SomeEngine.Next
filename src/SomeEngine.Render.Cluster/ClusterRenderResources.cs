using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Cluster;

/// <summary>Physical limits for one complete Cluster render-resource epoch.</summary>
public sealed record ClusterRenderOptions
{
    public ClusterResidencyOptions Residency { get; init; } = new();
}

/// <summary>Result of one Cluster residency-publication system update.</summary>
public readonly record struct ClusterPrepareResult(
    bool PublishedResidencyChanges);

/// <summary>Diagnostics captured from one active Cluster render-resource epoch.</summary>
public sealed record ClusterRenderDiagnostics(
    ClusterResidencyDiagnostics Residency,
    ClusterMeshCacheDiagnostics Meshes);

/// <summary>
/// Non-cacheable assembly-local binding for one exact Cluster epoch. Constructing the binding is
/// also the only operation that registers both Cluster timelines on a submitted render frame.
/// </summary>
internal readonly ref struct ClusterRenderBinding
{
    internal ClusterRenderBinding(
        ClusterResidencyBinding residency,
        RenderInstanceBatchView instanceProperties,
        ClusterEpochId readbackEpoch,
        RenderFrameUseLease frameUse)
    {
        PageHeap = residency.PageHeap;
        Bvh = residency.Bvh;
        PageFaultCapacity = residency.PageFaultCapacity;
        PropertyData = instanceProperties.PropertyData ??
            throw new ArgumentException(
                "Cluster rendering requires materialized instance-property data.",
                nameof(instanceProperties));
        InstancePropertyMetadata = instanceProperties.Metadata;
        InstancePropertyMetadataRange = instanceProperties.MetadataRange;
        DispatchExtent = instanceProperties.InstanceCount;
        ReadbackEpoch = readbackEpoch.IsValid
            ? readbackEpoch
            : throw new ArgumentException(
                "A Cluster render binding requires a valid readback epoch.",
                nameof(readbackEpoch));
        _frameUse = frameUse;
    }

    private readonly RenderFrameUseLease _frameUse;

    internal Buffer PageHeap { get; }

    internal Buffer Bvh { get; }

    internal int PageFaultCapacity { get; }

    internal Buffer PropertyData { get; }

    internal Buffer InstancePropertyMetadata { get; }

    internal BufferRange InstancePropertyMetadataRange { get; }

    internal int DispatchExtent { get; }

    internal ClusterEpochId ReadbackEpoch { get; }

    public void Dispose()
        => _frameUse.Dispose();
}

/// <summary>
/// Owns one Cluster algorithm-resource epoch. It borrows the Render-owned shared instance storage
/// and owns only residency, mesh preparation, and Cluster's instance-field contributor.
/// </summary>
public sealed class ClusterRenderResources : IDisposable
{
    private static readonly ConditionalWeakTable<RenderWorld, ClusterWorldClaim> s_worldClaims = new();

    private const int LifecycleActive = 0;
    private const int LifecycleStopping = 1;
    private const int LifecycleShutdown = 2;
    private const int LifecycleDisposed = 3;

    private readonly ClusterResidency _residency;
    private readonly RenderInstanceResources _instances;
    private readonly RenderInstanceBorrowerLease _instanceBorrower;
    private readonly RenderWorld _renderWorld;
    private readonly ClusterMeshCache _meshes;
    private readonly ClusterMeshPrepareSystem _meshPrepare;
    private readonly RenderTimeline _residencyTimeline;
    private readonly ClusterEpochId _epoch;
    private readonly ClusterWorldClaim _worldClaim;
    private readonly object _worldClaimToken = new();
    private readonly object _lifecycleGate = new();
    private readonly object _aggregateStateGate = new();
    private readonly Dictionary<int, int> _activeOperationThreadDepths = [];
    private readonly TimeSpan _shutdownTimeout;
    private RenderPrepareScope? _shutdownScope;
    private int _activeOperations;
    private bool _aggregateMutationActive;
    private int _lifecycleState;
    private int _shutdownExecuting;
    private bool _meshSystemShutdown;
    private bool _streamStopped;
    private bool _residencyDestroyed;
    private bool _instanceBorrowerReleased;

    public ClusterRenderResources(
        IGraphicsBackend backend,
        Device device,
        RenderFrameCoordinator coordinator,
        RenderWorld renderWorld,
        RenderInstanceStorageSystem instances,
        ClusterRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(renderWorld);
        ArgumentNullException.ThrowIfNull(instances);
        if (!ReferenceEquals(renderWorld, instances.World))
        {
            throw new ArgumentException(
                "Cluster and instance storage must use the same RenderWorld.",
                nameof(instances));
        }
        ClusterRenderOptions selected = options ?? new ClusterRenderOptions();
        ArgumentNullException.ThrowIfNull(selected.Residency);

        RenderInstanceBorrowerLease instanceBorrower = instances.AcquireBorrower();
        ClusterWorldClaim worldClaim;
        try
        {
            worldClaim = ClaimRenderWorld(renderWorld, _worldClaimToken);
        }
        catch
        {
            instanceBorrower.Dispose();
            throw;
        }

        ClusterResidency? residency = null;
        ClusterMeshCache? meshes = null;
        ClusterMeshPrepareSystem? meshPrepare = null;
        try
        {
            RenderTimeline residencyTimeline = coordinator.CreateTimeline(generationCount: 1);
            if (!ReferenceEquals(residencyTimeline.Device, device) ||
                !ReferenceEquals(instances.Timeline.Device, device))
            {
                throw new ArgumentException(
                    "Cluster resources and their frame coordinator must use the same device domain.",
                    nameof(coordinator));
            }
            if (!ReferenceEquals(instances.Timeline.Owner, residencyTimeline.Owner))
            {
                throw new ArgumentException(
                    "Cluster residency and shared instance storage must use the same exact frame coordinator.",
                    nameof(coordinator));
            }
            if (ReferenceEquals(residencyTimeline, instances.Timeline))
                throw new InvalidOperationException("Cluster requires distinct residency and instance timelines.");

            residency = new ClusterResidency(
                backend,
                device,
                residencyTimeline,
                selected.Residency);
            meshes = new ClusterMeshCache(residency);
            meshPrepare = new ClusterMeshPrepareSystem(renderWorld, meshes);

            _residency = residency;
            _instances = instances.Storage;
            _instanceBorrower = instanceBorrower;
            _renderWorld = renderWorld;
            _meshes = meshes;
            _meshPrepare = meshPrepare;
            _residencyTimeline = residencyTimeline;
            _epoch = residency.EpochId;
            _worldClaim = worldClaim;
            _shutdownTimeout = selected.Residency.DisposeTimeout;
        }
        catch
        {
            TryDispose(meshPrepare);
            TryDispose(residency);
            instanceBorrower.Dispose();
            ReleaseRenderWorld(worldClaim, _worldClaimToken);
            throw;
        }
    }

    internal ClusterResidency Residency => _residency;

    internal RenderWorld World => _renderWorld;

    internal RenderInstanceResources InstanceResources => _instances;

    internal RenderTimeline ResidencyTimeline => _residencyTimeline;

    internal RenderTimeline InstanceTimeline => _instances.Timeline;

    internal int PublishedMeshCount => _meshes.PublishedMeshCount;

    internal void EnterInstanceComposition() => EnterActiveOperation();

    internal void ExitInstanceComposition() => ExitActiveOperation();

    public ValueTask<ClusterMeshPrepareResult> PrepareMeshesAsync(
        AssetLoader assets,
        CancellationToken cancellationToken = default)
    {
        EnterActiveOperation(trackContext: false);
        try
        {
            ValueTask<ClusterMeshPrepareResult> pending =
                _meshPrepare.PrepareAsync(assets, cancellationToken);
            if (!pending.IsCompletedSuccessfully)
                return CompleteMeshPreparationAsync(pending);
            ClusterMeshPrepareResult result = pending.Result;
            ExitActiveOperation(trackContext: false);
            return ValueTask.FromResult(result);
        }
        catch
        {
            ExitActiveOperation(trackContext: false);
            throw;
        }
    }

    private async ValueTask<ClusterMeshPrepareResult> CompleteMeshPreparationAsync(
        ValueTask<ClusterMeshPrepareResult> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            ExitActiveOperation(trackContext: false);
        }
    }

    /// <summary>
    /// Publishes Cluster residency changes. Instance batches are built separately by a pipeline
    /// composition system while it holds a RenderWorld read snapshot; Cluster never owns a second
    /// instance store or writes persistent instance rows.
    /// </summary>
    internal ClusterPrepareResult Prepare(
        RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnterActiveOperation();
        try
        {
            lock (_aggregateStateGate)
            {
                if (_aggregateMutationActive)
                {
                    throw new InvalidOperationException(
                        "Cluster aggregate mutation cannot be entered recursively.");
                }
                _aggregateMutationActive = true;
                try
                {
                    using RenderTimelineLease residencyLease =
                        scope.AcquireTimeline(_residencyTimeline);

                    if (_residency.CaptureMeshesSnapshot().HasPendingPublication
                        && !_residencyTimeline.WaitForGeneration(0, _shutdownTimeout))
                    {
                        throw new TimeoutException(
                            "Cluster residency storage did not retire before pending publication.");
                    }
                    bool published = _residency.PublishPending();
                    return new ClusterPrepareResult(published);
                }
                finally
                {
                    _aggregateMutationActive = false;
                }
            }
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    internal ClusterGeometryProducer CreateGeometryProducer() => new(_meshes);

    /// <summary>
    /// Exposes a non-cacheable binding for the opaque metadata contract carried by the actual
    /// compiled shader variant. Cluster does not inspect or reconstruct that variant's fields.
    /// </summary>
    internal ClusterRenderBinding Use(
        RenderFrame frame,
        RenderInstanceBatch instanceProperties,
        RenderInstancePropertyLayout shaderContract)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(instanceProperties);
        ArgumentNullException.ThrowIfNull(shaderContract);
        EnterActiveOperation();
        RenderFrameUseLease? frameUse = null;
        try
        {
            // Validate the live batch and the exact shader ABI before registering either timeline
            // with the frame. A rejected binding must not change frame submission state.
            ClusterResidencyBinding residency = _residency.GetBinding();
            RenderInstanceBatchView batch = _instances.GetBatchView(
                instanceProperties,
                shaderContract,
                frameUse);
            frameUse = frame.AcquireUse([_residencyTimeline, _instances.Timeline]);
            frameUse.RegisterGeneration(_residencyTimeline, 0);
            ClusterRenderBinding binding = new(
                residency,
                batch,
                _epoch,
                frameUse);
            frameUse = null;
            return binding;
        }
        finally
        {
            frameUse?.Dispose();
            ExitActiveOperation();
        }
    }

    /// <summary>Ingests page faults only when tagged with this exact resource epoch.</summary>
    internal void IngestPageFaultReadback(
        ClusterEpochId epoch,
        ReadOnlySpan<byte> bytes)
    {
        EnterActiveOperation();
        try
        {
            ValidateReadbackEpoch(epoch);
            _residency.IngestFaults(bytes);
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    public void PumpStreaming()
    {
        EnterActiveOperation();
        try
        {
            _residency.PumpStreaming();
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    public bool TryGetFaultReplayRequest(out ulong generation)
    {
        EnterActiveOperation();
        try
        {
            return _residency.TryGetFaultReplayRequest(out generation);
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    public void AcknowledgeFaultReplay(ulong generation)
    {
        EnterActiveOperation();
        try
        {
            _residency.AcknowledgeFaultReplay(generation);
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    internal void IngestPageUsageReadback(
        ClusterEpochId epoch,
        ReadOnlySpan<uint> leafNodeIndices)
    {
        EnterActiveOperation();
        try
        {
            ValidateReadbackEpoch(epoch);
            _residency.ReportLeafUsage(leafNodeIndices);
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    public ClusterRenderDiagnostics CaptureDiagnostics()
    {
        EnterActiveOperation();
        try
        {
            lock (_aggregateStateGate)
            {
                if (_aggregateMutationActive)
                {
                    throw new InvalidOperationException(
                        "Cluster diagnostics cannot be captured during aggregate mutation.");
                }
                _residency.CaptureDiagnostics(
                    out ClusterResidencyDiagnostics residency,
                    out ClusterMeshCacheDiagnostics meshes);
                return new ClusterRenderDiagnostics(
                    residency,
                    meshes);
            }
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    /// <summary>
    /// Stops Cluster contributors and destroys Cluster-owned residency while its exact timeline is
    /// quiescent. Shared batch storage deliberately outlives this pipeline epoch.
    /// </summary>
    public void Shutdown(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (IsCurrentThreadInActiveOperation() || _residency.IsRegistrationOwner)
        {
            throw new InvalidOperationException(
                "A Cluster operation cannot shut down its own resource epoch.");
        }
        int state = BeginShutdownExecution();
        try
        {
            // Only Cluster residency is owned here. Shared instance storage has its own explicit
            // shutdown boundary and must not be retired by a pipeline.
            using RenderTimelineLease residencyLease = scope.AcquireTimeline(_residencyTimeline);
            state = EnterStopping(state);

            long started = Stopwatch.GetTimestamp();
            Task registrationIdle = _residency.StopRegistrations();
            WaitForActiveOperations(started);
            if (!_instanceBorrowerReleased)
            {
                // Stopping rejects new Cluster operations, and every operation that could touch
                // shared instance storage has drained. Cluster-owned cleanup must not keep that
                // independent storage epoch hostage if a later cleanup step is retryable.
                _instanceBorrower.Dispose();
                _instanceBorrowerReleased = true;
            }
            WaitForRegistrationIdle(registrationIdle, Remaining(started));

            if (state == LifecycleShutdown)
            {
                lock (_lifecycleGate)
                    _shutdownScope = scope;
                return;
            }

            if (!_meshSystemShutdown)
            {
                _meshPrepare.Dispose();
                _meshSystemShutdown = true;
            }
            if (!_streamStopped)
            {
                _residency.StopStreaming(Remaining(started));
                _streamStopped = true;
            }
            if (!_residencyDestroyed)
            {
                _residency.DestroyStorage();
                _residencyDestroyed = true;
            }
            lock (_lifecycleGate)
            {
                _shutdownScope = scope;
                _lifecycleState = LifecycleShutdown;
            }
        }
        finally
        {
            EndShutdownExecution();
        }
    }

    /// <summary>Disposal is valid only after an explicit, committed shutdown boundary.</summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_shutdownExecuting != 0)
            {
                throw new InvalidOperationException(
                    "Cluster resource shutdown is currently in progress.");
            }
            switch (_lifecycleState)
            {
                case LifecycleActive:
                    throw new InvalidOperationException(
                        "Cluster resources must be shut down at a render prepare boundary before disposal.");
                case LifecycleStopping:
                    throw new InvalidOperationException(
                        "Cluster resource shutdown has not completed.");
                case LifecycleShutdown:
                    if (_shutdownScope is null || !_shutdownScope.IsCommitted)
                    {
                        throw new InvalidOperationException(
                            "The prepare scope used for Cluster shutdown must commit before disposal.");
                    }
                    _lifecycleState = LifecycleDisposed;
                    ReleaseRenderWorld(_worldClaim, _worldClaimToken);
                    return;
                case LifecycleDisposed:
                    return;
                default:
                    throw new InvalidOperationException("Unknown Cluster render-resource lifecycle.");
            }
        }
    }

    private int BeginShutdownExecution()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState == LifecycleDisposed)
                throw new ObjectDisposedException(nameof(ClusterRenderResources));
            if (_shutdownExecuting != 0)
                throw new InvalidOperationException("Cluster resource shutdown is already in progress.");
            _shutdownExecuting = 1;
            return _lifecycleState;
        }
    }

    private int EnterStopping(int observedState)
    {
        lock (_lifecycleGate)
        {
            if (_shutdownExecuting == 0)
                throw new InvalidOperationException("Cluster resource shutdown ownership was lost.");
            if (_lifecycleState != observedState)
                throw new InvalidOperationException("Cluster resource lifecycle changed during shutdown validation.");
            if (_lifecycleState == LifecycleActive)
                _lifecycleState = LifecycleStopping;
            return _lifecycleState;
        }
    }

    private void EndShutdownExecution()
    {
        lock (_lifecycleGate)
        {
            if (_shutdownExecuting == 0)
                throw new InvalidOperationException("Cluster resource shutdown is not in progress.");
            _shutdownExecuting = 0;
            Monitor.PulseAll(_lifecycleGate);
        }
    }

    private void EnterActiveOperation(bool trackContext = true)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != LifecycleActive)
                throw new ObjectDisposedException(nameof(ClusterRenderResources));
            _activeOperations = checked(_activeOperations + 1);
            if (trackContext)
            {
                int threadId = Environment.CurrentManagedThreadId;
                _activeOperationThreadDepths.TryGetValue(threadId, out int depth);
                _activeOperationThreadDepths[threadId] = checked(depth + 1);
            }
        }
    }

    private void ExitActiveOperation(bool trackContext = true)
    {
        lock (_lifecycleGate)
        {
            if (_activeOperations <= 0)
                throw new InvalidOperationException("Cluster active-operation count is already zero.");
            if (trackContext)
            {
                int threadId = Environment.CurrentManagedThreadId;
                if (!_activeOperationThreadDepths.TryGetValue(threadId, out int depth) || depth <= 0)
                    throw new InvalidOperationException("Cluster active-operation depth is already zero.");
                if (depth == 1)
                    _activeOperationThreadDepths.Remove(threadId);
                else
                    _activeOperationThreadDepths[threadId] = depth - 1;
            }
            _activeOperations--;
            if (_activeOperations == 0)
                Monitor.PulseAll(_lifecycleGate);
        }
    }

    private bool IsCurrentThreadInActiveOperation()
    {
        lock (_lifecycleGate)
        {
            return _activeOperationThreadDepths.TryGetValue(
                Environment.CurrentManagedThreadId,
                out int depth)
                && depth != 0;
        }
    }

    private void WaitForActiveOperations(long started)
    {
        lock (_lifecycleGate)
        {
            while (_activeOperations != 0)
            {
                TimeSpan remaining = Remaining(started);
                if (!Monitor.Wait(_lifecycleGate, remaining) && _activeOperations != 0)
                {
                    throw new TimeoutException(
                        $"Cluster operations did not drain within {_shutdownTimeout}.");
                }
            }
        }
    }

    private static void WaitForRegistrationIdle(Task idle, TimeSpan timeout)
    {
        try
        {
            idle.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Cluster mesh registration did not drain within {timeout}.",
                error);
        }
    }

    private TimeSpan Remaining(long started)
    {
        TimeSpan remaining = _shutdownTimeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException($"Cluster shutdown exceeded its {_shutdownTimeout} deadline.");
        return remaining;
    }

    private void ValidateReadbackEpoch(ClusterEpochId epoch)
    {
        if (epoch != _epoch)
        {
            throw new ArgumentException(
                $"The readback belongs to Cluster epoch {epoch}, not {_epoch}.",
                nameof(epoch));
        }
    }

    private static ClusterWorldClaim ClaimRenderWorld(RenderWorld renderWorld, object token)
    {
        ClusterWorldClaim claim = s_worldClaims.GetValue(
            renderWorld,
            static _ => new ClusterWorldClaim());
        lock (claim.Gate)
        {
            if (claim.OwnerToken is not null)
            {
                throw new InvalidOperationException(
                    "A RenderWorld can own only one active Cluster resource epoch.");
            }
            claim.OwnerToken = token;
            return claim;
        }
    }

    private static void ReleaseRenderWorld(ClusterWorldClaim claim, object token)
    {
        lock (claim.Gate)
        {
            if (!ReferenceEquals(claim.OwnerToken, token))
                throw new InvalidOperationException("The RenderWorld Cluster claim is not owned by this epoch.");
            claim.OwnerToken = null;
        }
    }

    private static void TryDispose(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
        }
    }

    internal readonly struct ClusterGeometryProducer : IRenderInstanceProducer
    {
        private readonly ClusterMeshCache _meshes;
        private readonly ResolvedRenderInstanceProperty<RenderTransform> _currentTransform;
        private readonly ResolvedRenderInstanceProperty<RenderPreviousTransform> _previousTransform;
        private readonly ResolvedRenderInstanceProperty<uint> _bvhRoot;
        private readonly ResolvedRenderInstanceProperty<float> _boundsExpansion;

        internal ClusterGeometryProducer(ClusterMeshCache meshes)
        {
            _meshes = meshes;
            RenderInstancePropertyLayout clusterProperties = ClusterRenderFeature.GeometryLayout;
            _currentTransform = clusterProperties.Resolve<RenderTransform>(
                RenderInstanceTransformProperties.CurrentTransformKey);
            _previousTransform = clusterProperties.Resolve<RenderPreviousTransform>(
                RenderInstanceTransformProperties.PreviousTransformKey);
            _bvhRoot = clusterProperties.Resolve<uint>(ClusterRenderFeature.BvhRootKey);
            _boundsExpansion = clusterProperties.Resolve<float>(ClusterRenderFeature.BoundsExpansionKey);
        }

        public RenderInstancePropertyLayout Properties => ClusterRenderFeature.GeometryLayout;

        public RenderInstanceChanges GetChanges(
            ReadOnlyQueryPacket packet,
            uint lastSystemVersion) =>
            packet.ChangedSince<RenderTransform>(lastSystemVersion)
            || packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
            || packet.ChangedSince<RenderMesh>(lastSystemVersion)
                ? RenderInstanceChanges.Values
                : RenderInstanceChanges.None;

        public void Bind(RenderInstanceWriteSlice destination)
        {
            destination.BindPerInstance(_currentTransform);
            destination.BindPerInstance(_previousTransform);
            destination.BindPerInstance(_bvhRoot);
            destination.BindPerInstance(_boundsExpansion);
        }

        public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
        {
            destination.Write(_currentTransform, packet.Read<RenderTransform>());
            destination.Write(_previousTransform, packet.Read<RenderPreviousTransform>());

            ReadOnlySpan<RenderMesh> renderMeshes = packet.Read<RenderMesh>();
            uint[]? rentedRoots = null;
            float[]? rentedExpansions = null;
            scoped Span<uint> roots;
            scoped Span<float> expansions;
            if (packet.Count <= 1_024)
            {
                roots = stackalloc uint[packet.Count];
                expansions = stackalloc float[packet.Count];
            }
            else
            {
                rentedRoots = ArrayPool<uint>.Shared.Rent(packet.Count);
                rentedExpansions = ArrayPool<float>.Shared.Rent(packet.Count);
                roots = rentedRoots.AsSpan(0, packet.Count);
                expansions = rentedExpansions.AsSpan(0, packet.Count);
            }
            try
            {
                _meshes.ResolveInstanceGeometry(
                    packet.Entities,
                    renderMeshes,
                    roots,
                    expansions);
                destination.Write(_bvhRoot, roots);
                destination.Write(_boundsExpansion, expansions);
            }
            finally
            {
                if (rentedRoots is not null)
                    ArrayPool<uint>.Shared.Return(rentedRoots);
                if (rentedExpansions is not null)
                    ArrayPool<float>.Shared.Return(rentedExpansions);
            }
        }
    }

    private sealed class ClusterWorldClaim
    {
        internal object Gate { get; } = new();

        internal object? OwnerToken { get; set; }
    }
}
