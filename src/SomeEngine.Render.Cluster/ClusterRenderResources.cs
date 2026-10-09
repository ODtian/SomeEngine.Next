using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

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
/// Cluster residency and instance data bound during a shared render-data read.
/// </summary>
internal readonly ref struct ClusterRenderBinding
{
    internal ClusterRenderBinding(
        ClusterResidencyBinding residency,
        RenderInstanceBatchView instanceProperties,
        ClusterEpochId readbackEpoch)
    {
        PageFaultCapacity = residency.PageFaultCapacity;
        ResidentPageRanges = residency.ResidentPageRanges;
        BvhRange = residency.BvhRange;
        Publication = residency.Publication;
        InstancePropertyMetadataRange = instanceProperties.MetadataRange;
        DispatchExtent = instanceProperties.InstanceCount;
        ReadbackEpoch = readbackEpoch.IsValid
            ? readbackEpoch
            : throw new ArgumentException(
                "A Cluster render binding requires a valid readback epoch.",
                nameof(readbackEpoch));
    }

    internal int PageFaultCapacity { get; }

    internal BufferRange[] ResidentPageRanges { get; }

    internal BufferRange BvhRange { get; }

    internal ClusterResidencyPublication? Publication { get; }

    internal BufferRange InstancePropertyMetadataRange { get; }

    internal int DispatchExtent { get; }

    internal ClusterEpochId ReadbackEpoch { get; }
}

/// <summary>
/// Owns one Cluster algorithm-resource epoch. It borrows the Render-owned shared instance storage
/// and owns only residency, mesh preparation, and Cluster's instance-field contributor.
/// </summary>
public sealed class ClusterRenderResources : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite UseTrace = new("render", "Cluster.ResourceUse");
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
    private readonly ClusterEpochId _epoch;
    private readonly ClusterWorldClaim _worldClaim;
    private readonly object _worldClaimToken = new();
    private readonly object _lifecycleGate = new();
    private readonly object _aggregateStateGate = new();
    private readonly object _feedbackGate = new();
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
    private ulong _lastFaultFeedbackSequence;
    private ulong _lastUsageFeedbackSequence;

    public ClusterRenderResources(
        IGraphicsBackend backend,
        Device device,
        RenderWorld renderWorld,
        RenderInstanceStorageSystem instances,
        ClusterRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
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
            residency = new ClusterResidency(
                backend,
                device,
                selected.Residency);
            meshes = new ClusterMeshCache(residency);
            meshPrepare = new ClusterMeshPrepareSystem(renderWorld, meshes);

            _residency = residency;
            _instances = instances.Storage;
            _instanceBorrower = instanceBorrower;
            _renderWorld = renderWorld;
            _meshes = meshes;
            _meshPrepare = meshPrepare;
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

    internal int PublishedMeshCount => _meshes.PublishedMeshCount;

    internal int HierarchyLevels => _residency.HierarchyLevels;
    internal uint MaximumRasterBatchesPerCluster => _residency.MaximumRasterBatchesPerCluster;

    internal int PageFaultCapacity => _residency.PageFaultCapacity;
    internal uint GeometryPageCount => _residency.GeometryPageCount;
    public ReadOnlyMemory<ClusterLodChange> LodChanges => _residency.LodChanges;

    internal void EnterInstanceComposition() => EnterActiveOperation();

    internal void ExitInstanceComposition() => ExitActiveOperation();

    public ValueTask<ClusterMeshPrepareResult> PrepareMeshesAsync(
        CancellationToken cancellationToken = default)
    {
        EnterActiveOperation(trackContext: false);
        try
        {
            ValueTask<ClusterMeshPrepareResult> pending =
                _meshPrepare.PrepareAsync(cancellationToken);
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
        DevelopmentCheck.Require(scope is not null && !scope.IsClosed, "Cluster mutation requires an open prepare scope.");
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
                    bool prepared = _residency.PreparePublication();
                    return new ClusterPrepareResult(prepared);
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

    internal void CommitResidencyPublication(
        ClusterResidencyPublication publication,
        QueueCompletion[][] retirementCompletions)
    {
        EnterActiveOperation();
        try
        {
            lock (_aggregateStateGate)
            {
                if (_aggregateMutationActive)
                    throw new InvalidOperationException("Cluster aggregate mutation is already active.");
                _aggregateMutationActive = true;
                try
                {
                    _residency.CommitPublication(publication, retirementCompletions);
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

    internal void RetainResidencyPublication(ClusterResidencyPublication publication)
    {
        EnterActiveOperation();
        try
        {
            _residency.RetainPublication(publication);
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
        RenderReadScope readScope,
        RenderInstanceBatch instanceProperties,
        RenderInstancePropertyLayout shaderContract)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UseTrace);
        ArgumentNullException.ThrowIfNull(instanceProperties);
        ArgumentNullException.ThrowIfNull(shaderContract);
        EnterActiveOperation();
        try
        {
            DevelopmentCheck.Require(!readScope.IsClosed, "Cluster binding requires an open render-data read.");
            ClusterResidencyBinding residency = _residency.GetBinding();
            RenderInstanceBatchView batch = _instances.GetBatchView(instanceProperties, shaderContract);
            return new ClusterRenderBinding(residency, batch, _epoch);
        }
        finally
        {
            ExitActiveOperation();
        }
    }

    /// <summary>Ingests all page-fault producers from one later host visit to a submitted frame.</summary>
    internal void IngestPageFaultReadbackBatch(
        ClusterReadbackTicket ticket,
        ReadOnlySpan<byte[]> payloads)
    {
        if (payloads.IsEmpty)
            throw new ArgumentException("Cluster page-fault feedback must contain a producer payload.", nameof(payloads));
        EnterActiveOperation();
        try
        {
            lock (_feedbackGate)
            {
                ValidateReadbackTicket(ticket, _lastFaultFeedbackSequence);
                foreach (byte[] payload in payloads)
                {
                    if (payload is null)
                        throw new ArgumentException("Cluster page-fault feedback contains a null payload.", nameof(payloads));
                    _residency.IngestFaults(payload);
                }
                _residency.UpdateStreamingQuality();
                _lastFaultFeedbackSequence = ticket.FeedbackSequence;
            }
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

    internal void IngestPageUsageReadback(
        ClusterReadbackTicket ticket,
        ReadOnlySpan<uint> leafNodeIndices)
    {
        EnterActiveOperation();
        try
        {
            lock (_feedbackGate)
            {
                ValidateReadbackTicket(ticket, _lastUsageFeedbackSequence);
                _residency.ReportLeafUsage(leafNodeIndices);
                _lastUsageFeedbackSequence = ticket.FeedbackSequence;
            }
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
    /// Stops Cluster contributors and releases residency under exclusive render-data access.
    /// </summary>
    public void Shutdown(RenderPrepareScope scope)
    {
        DevelopmentCheck.Require(scope is not null && !scope.IsClosed, "Cluster mutation requires an open prepare scope.");
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

    /// <summary>Disposal follows shutdown and release of its prepare scope.</summary>
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
                    if (_shutdownScope is null || !_shutdownScope.IsClosed)
                    {
                        throw new InvalidOperationException(
                            "The prepare scope used for Cluster shutdown must be released before disposal.");
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

    private void ValidateReadbackTicket(
        ClusterReadbackTicket ticket,
        ulong lastFeedbackSequence)
    {
        if (!ticket.IsValid)
            throw new ArgumentException("The Cluster readback ticket is invalid.", nameof(ticket));
        if (ticket.EpochId != _epoch)
        {
            throw new ArgumentException(
                $"The readback belongs to Cluster epoch {ticket.EpochId}, not {_epoch}.",
                nameof(ticket));
        }
        if (ticket.FeedbackSequence <= lastFeedbackSequence)
        {
            throw new ArgumentException(
                $"Cluster feedback sequence {ticket.FeedbackSequence} is not newer than " +
                $"the consumed sequence {lastFeedbackSequence}.",
                nameof(ticket));
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
        private readonly ResolvedRenderInstanceProperty<uint> _depthCacheClass;
        private readonly ResolvedRenderInstanceProperty<FirstPersonMeshData> _firstPerson;

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
            _depthCacheClass = clusterProperties.Resolve<uint>(ClusterRenderFeature.DepthCacheClassKey);
            _firstPerson = clusterProperties.Resolve<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey);
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
            => Bind(destination, true, true, true);

        internal void Bind(RenderInstanceWriteSlice destination, bool current, bool previous, bool mesh)
        {
            if (current) destination.BindPerInstance(_currentTransform);
            if (previous) destination.BindPerInstance(_previousTransform);
            if (mesh)
            {
                destination.BindPerInstance(_bvhRoot);
                destination.BindPerInstance(_boundsExpansion);
                destination.BindPerInstance(_depthCacheClass);
                destination.BindPerInstance(_firstPerson);
            }
        }

        public void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet)
            => Write(destination, packet, true, true, true);

        internal void Write(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
            bool current, bool previous, bool mesh)
        {
            if (current) destination.Write(_currentTransform, packet.Read<RenderTransform>());
            if (previous) destination.Write(_previousTransform, packet.Read<RenderPreviousTransform>());
            if (!mesh) return;

            Span<uint> depthClasses = destination.GetWriteSpan(_depthCacheClass);
            Span<FirstPersonMeshData> firstPerson = destination.GetWriteSpan(_firstPerson);
            ReadOnlySpan<RenderMesh> meshValues = packet.Read<RenderMesh>();
            for (int row = 0; row < meshValues.Length; row++)
            {
                depthClasses[row] = (uint)meshValues[row].DepthCacheClass;
                firstPerson[row] = meshValues[row].FirstPerson;
            }
            _meshes.ResolveInstanceGeometry(packet.Entities, meshValues,
                destination.GetWriteSpan(_bvhRoot), destination.GetWriteSpan(_boundsExpansion));
        }

        public void WriteChanged(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
            uint lastSystemVersion, bool allTransformsChanged = false)
        {
            if (allTransformsChanged)
                Write(destination, packet, true, true, false);
            else
            {
                WriteChangedProperty(destination, packet, _currentTransform, lastSystemVersion);
                WriteChangedProperty(destination, packet, _previousTransform, lastSystemVersion);
            }
            if (!packet.ChangedSince<RenderMesh>(lastSystemVersion)) return;
            // RenderMesh is one ECS change column. Its four derived properties
            // share these changed rows; transform columns keep their own ranges.
            int start = 0;
            while (NextChangedRows<RenderMesh>(packet, lastSystemVersion, ref start, out int count))
            {
                Write(destination.Slice(start, count), packet.Slice(start, count), false, false, true);
                start += count;
            }
        }

        private static void WriteChangedProperty<T>(RenderInstanceWriteSlice destination, ReadOnlyQueryPacket packet,
            ResolvedRenderInstanceProperty<T> property, uint lastSystemVersion) where T : unmanaged
        {
            if (packet.AllRowsChangedSince<T>(lastSystemVersion))
            {
                destination.Write(property, packet.Read<T>());
                return;
            }
            int start = 0;
            while (NextChangedRows<T>(packet, lastSystemVersion, ref start, out int count))
            {
                destination.Slice(start, count).Write(property, packet.Read<T>().Slice(start, count));
                start += count;
            }
        }

        private static bool NextChangedRows<T>(ReadOnlyQueryPacket packet, uint lastSystemVersion,
            ref int start, out int count) where T : struct
        {
            while (start < packet.Count && !packet.Slice(start, 1).ChangedSince<T>(lastSystemVersion)) start++;
            int end = start;
            while (end < packet.Count && packet.Slice(end, 1).ChangedSince<T>(lastSystemVersion)) end++;
            count = end - start;
            return count != 0;
        }
    }

    private sealed class ClusterWorldClaim
    {
        internal object Gate { get; } = new();

        internal object? OwnerToken { get; set; }
    }
}
