using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Render.Cluster;

internal enum PageLoadResult : byte
{
    Staged,
    AlreadyTracked,
    Deferred,
    UnknownPage,
    NoCapacity,
}

internal sealed class ClusterPageSourceException : Exception
{
    internal ClusterPageSourceException(uint pageId, Exception innerException)
        : base($"Reading Cluster page {pageId} into final storage failed.", innerException)
    {
    }
}

internal readonly record struct ClusterMeshRegistration(
    Mesh Mesh,
    uint FirstPageId,
    uint PageCount,
    uint RootNode);

internal sealed partial class ClusterMeshes : IDisposable
{
    private static long s_nextEpochId;

    private readonly PageHeap _heap;
    private readonly object _gate = new();
    private readonly ClusterBvh _bvh;
    private readonly MeshPages _pages = new();
    private readonly ResidencyBudgetLedger _residency;
    private readonly IClusterPageStorage _pageStorage;
    private readonly Dictionary<uint, DirectPageLoad> _directLoads = new();
    private readonly Dictionary<uint, DirectPageBacking> _directPageBackings = new();
    private readonly List<DirectPageBacking> _quarantinedPageBackings = [];
    private ulong _stateRevision;
    private ulong _cleanupFailureSequence;
    private uint _evictedPageCount;
    private int _activePageStreams;
    private int _disposed;
    private ClusterCleanupFailure? _lastCleanupFailure;
    private ClusterMeshesSnapshot? _terminalSnapshot;

    public ClusterEpochId EpochId { get; }
    internal ResidencyBudgetLedger ResidencyContext
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _residency;
            }
        }
    }

    public ClusterMeshes()
        : this(PageHeap.CapacityBytes, residency: null)
    {
    }

    internal ClusterMeshes(uint pageHeapCapacity)
        : this(pageHeapCapacity, residency: null)
    {
    }

    internal ClusterMeshes(
        uint pageHeapCapacity,
        ResidencyBudgetLedger? residency,
        IClusterPageStorage? pageStorage = null,
        IClusterBvhStorage? bvhStorage = null)
    {
        long epochValue = Interlocked.Increment(ref s_nextEpochId);
        if (epochValue <= 0)
            throw new InvalidOperationException("Cluster epoch id space is exhausted.");
        EpochId = new ClusterEpochId(checked((ulong)epochValue));
        _heap = new PageHeap(pageHeapCapacity);
        _bvh = new ClusterBvh(bvhStorage);
        _residency = residency ?? new ResidencyBudgetLedger();
        _pageStorage = pageStorage ?? new SparseClusterPageStorage(pageHeapCapacity);
        _stateRevision = 1;
    }

    private sealed class DirectPageLoad
    {
        internal DirectPageLoad(
            uint offset,
            uint size,
            Memory<byte> destination,
            ResidencyReservation reservation,
            MeshPageReadSource source)
        {
            Offset = offset;
            Size = size;
            Destination = destination;
            Reservation = reservation;
            Source = source;
        }

        internal uint Offset { get; }
        internal uint Size { get; }
        internal Memory<byte> Destination { get; }
        internal ResidencyReservation? Reservation { get; set; }
        internal MeshPageReadSource Source { get; }
    }

    private readonly record struct DirectPageBacking(uint Offset, uint Size);

    public ClusterMeshesSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            if (_disposed != 0)
                return _terminalSnapshot ?? throw new InvalidOperationException("Disposed Cluster epoch has no terminal snapshot.");
            return CreateSnapshot(ClusterLifecycle.Active);
        }
    }

    internal void AttachPageStream()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _activePageStreams = checked(_activePageStreams + 1);
            AdvanceRevision();
        }
    }

    internal void DetachPageStream()
    {
        lock (_gate)
        {
            if (_activePageStreams <= 0)
                throw new InvalidOperationException("The Cluster page-stream ownership count is already zero.");
            _activePageStreams--;
            AdvanceRevision();
        }
    }

    public PageFaultResolution ResolvePageFault(uint leafNodeIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_bvh.TryPageForLeaf(leafNodeIndex, out uint pageID))
                return new PageFaultResolution(PageFaultResolutionKind.Unknown, 0, 0);
            return ResolvePageCore(pageID);
        }
    }

    internal PageFaultResolution ResolvePage(uint pageID)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ResolvePageCore(pageID);
        }
    }

    private PageFaultResolution ResolvePageCore(uint pageID)
    {
        if (!_pages.TrySource(pageID, out uint size, out _))
            return new PageFaultResolution(PageFaultResolutionKind.Unknown, 0, 0);

        if (_pages.IsResident(pageID) && !_pages.IsRetiring(pageID))
        {
            _pages.Touch(pageID);
            return new PageFaultResolution(PageFaultResolutionKind.Satisfied, pageID, size);
        }

        if (_pages.IsEvictionPending(pageID))
        {
            _bvh.ReservePagePatch(pageID);
            ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
            if (!_pages.TryCancelPendingEviction(pageID, out uint offset))
                throw new InvalidOperationException("Pending page eviction could not be cancelled atomically.");
            try
            {
                _bvh.PatchPage(pageID, offset);
            }
            catch
            {
                _bvh.RestoreChanges(bvhCheckpoint);
                if (!_pages.StageEviction(pageID, out _))
                    throw new InvalidOperationException("Cancelled page eviction could not be rolled back atomically.");
                throw;
            }
            AdvanceRevision();
            return new PageFaultResolution(PageFaultResolutionKind.Satisfied, pageID, size);
        }

        if (_directLoads.ContainsKey(pageID) || _pages.IsLoadPending(pageID))
            return new PageFaultResolution(PageFaultResolutionKind.Pending, pageID, size);

        return new PageFaultResolution(PageFaultResolutionKind.NeedsLoad, pageID, size);
    }

    /// <summary>
    /// Reserves the final page-heap range before IO, reads the page directly into that range, and
    /// only then makes it eligible for publication. No upload-staging page exists on this path.
    /// </summary>
    internal async ValueTask<PageLoadResult> LoadPageIntoFinalOwnerAsync(
        uint pageID,
        CancellationToken cancellationToken = default)
    {
        DirectPageLoad load;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pages.IsRetiring(pageID))
                return PageLoadResult.Deferred;
            if (_pages.IsResident(pageID) || _pages.IsLoadPending(pageID) || _directLoads.ContainsKey(pageID))
                return PageLoadResult.AlreadyTracked;
            if (!_pages.TrySource(pageID, out uint expectedSize, out MeshPageReadSource source))
                return PageLoadResult.UnknownPage;
            if (!_heap.CanFit(expectedSize))
            {
                if (_pages.PendingEvictionCount != 0)
                    return PageLoadResult.Deferred;
                return TryEvictPage(pageID)
                    ? PageLoadResult.Deferred
                    : PageLoadResult.NoCapacity;
            }

            long gpuBytes = PageHeap.AllocationSize(expectedSize);
            if (gpuBytes > _residency.Budget(ResidencyClass.Gpu))
                return PageLoadResult.NoCapacity;
            if (!_residency.TryReserve(ResidencyClass.Gpu, gpuBytes, out ResidencyReservation? reservation))
            {
                if (_pages.PendingEvictionCount == 0)
                    _ = TryEvictPage(pageID);
                return PageLoadResult.Deferred;
            }

            Exception? cleanupFailure = null;
            try
            {
                _heap.ReserveFrees(1);
                _directLoads.EnsureCapacity(checked(_directLoads.Count + 1));
                _quarantinedPageBackings.EnsureCapacity(checked(
                    _quarantinedPageBackings.Count + 1));
                if (!_heap.TryAlloc(expectedSize, out uint offset))
                {
                    if (_pages.PendingEvictionCount == 0)
                        _ = TryEvictPage(pageID);
                    return PageLoadResult.Deferred;
                }
                bool storageAllocated = false;
                try
                {
                    Memory<byte> destination = _pageStorage.Allocate(offset, checked((int)expectedSize));
                    storageAllocated = true;
                    if (destination.Length != checked((int)expectedSize))
                    {
                        throw new InvalidOperationException(
                            $"Final Cluster page storage returned {destination.Length} bytes for a {expectedSize}-byte allocation.");
                    }
                    load = new DirectPageLoad(offset, expectedSize, destination, reservation!, source);
                    _directLoads.Add(pageID, load);
                    storageAllocated = false;
                    reservation = null;
                    AdvanceRevision();
                }
                catch
                {
                    bool canFreeHeap = true;
                    if (storageAllocated)
                    {
                        try
                        {
                            _pageStorage.Release(offset, checked((int)expectedSize));
                        }
                        catch (Exception error)
                        {
                            // An uncertain final-storage owner keeps its heap range quarantined
                            // until epoch disposal. Reusing it could alias a live mapped range.
                            _quarantinedPageBackings.Add(new DirectPageBacking(offset, expectedSize));
                            cleanupFailure = error;
                            canFreeHeap = false;
                        }
                    }
                    if (canFreeHeap)
                    {
                        try
                        {
                            _heap.Free(offset, expectedSize);
                        }
                        catch (Exception error)
                        {
                            cleanupFailure ??= error;
                        }
                    }
                    throw;
                }

            }
            finally
            {
                if (reservation is not null)
                {
                    try
                    {
                        reservation.Dispose();
                    }
                    catch (Exception error)
                    {
                        cleanupFailure ??= error;
                    }
                }
                if (cleanupFailure is not null)
                    RecordCleanupFailure(ClusterCleanupStage.PageLoad, cleanupFailure);
            }
        }

        try
        {
            try
            {
                await load.Source.ReadIntoAsync(load.Destination, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ClusterPageSourceException(pageID, error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateStreamedPage(load.Destination.Span, load.Source);

            lock (_gate)
            {
                ThrowIfDisposed();
                if (!_directLoads.TryGetValue(pageID, out DirectPageLoad? current) ||
                    !ReferenceEquals(current, load))
                {
                    throw new InvalidOperationException($"Cluster page {pageID} no longer owns its final destination.");
                }

                ResidencyReservation gpuReservation = load.Reservation
                    ?? throw new InvalidOperationException($"Cluster page {pageID} final destination was already committed.");
                _directPageBackings.EnsureCapacity(checked(_directPageBackings.Count + 1));
                _bvh.ReservePagePatch(pageID);
                ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
                bool staged = false;
                bool backingTracked = false;
                try
                {
                    _pages.StageResident(pageID, load.Offset, gpuReservation);
                    staged = true;
                    _pageStorage.Stage(load.Offset, checked((int)load.Size));
                    _directPageBackings.Add(pageID, new DirectPageBacking(load.Offset, load.Size));
                    backingTracked = true;
                    _bvh.PatchPage(pageID, load.Offset);
                    load.Reservation = null;
                    _directLoads.Remove(pageID);
                    AdvanceRevision();
                    return PageLoadResult.Staged;
                }
                catch
                {
                    _bvh.RestoreChanges(bvhCheckpoint);
                    if (backingTracked)
                        _directPageBackings.Remove(pageID);
                    if (staged)
                        _ = _pages.CancelStagedResident(pageID, gpuReservation);
                    throw;
                }
            }
        }
        catch
        {
            CancelDirectLoad(pageID, load);
            throw;
        }
    }

    private void CancelDirectLoad(uint pageID, DirectPageLoad expected)
    {
        ResidencyReservation? reservation = null;
        Exception? cleanupFailure = null;
        lock (_gate)
        {
            if (_directLoads.TryGetValue(pageID, out DirectPageLoad? current) && ReferenceEquals(current, expected))
            {
                _directLoads.Remove(pageID);
                try
                {
                    _pageStorage.Release(expected.Offset, checked((int)expected.Size));
                    _heap.Free(expected.Offset, expected.Size);
                }
                catch (Exception error)
                {
                    // A failed final-storage release must not make the offset reusable. The
                    // storage owner will still clear all remaining ranges at epoch disposal.
                    cleanupFailure = error;
                }
                reservation = expected.Reservation;
                expected.Reservation = null;
                AdvanceRevision();
            }
        }
        if (reservation is not null)
        {
            try
            {
                reservation.Dispose();
            }
            catch (Exception error)
            {
                cleanupFailure ??= error;
            }
        }
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.PageLoad, cleanupFailure);
    }

    public bool EvictPage(uint pageID)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _bvh.ReservePagePatch(pageID);
            if (!_pages.StageEviction(pageID, out _))
                return false;

            ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
            try
            {
                _bvh.PatchPage(pageID, ClusterBvh.PageFaultMarker);
            }
            catch
            {
                _bvh.RestoreChanges(bvhCheckpoint);
                if (!_pages.TryCancelPendingEviction(pageID, out _))
                    throw new InvalidOperationException("Pending page eviction could not be rolled back atomically.");
                throw;
            }
            AdvanceRevision();
            return true;
        }
    }

    internal void ReportLeafUsage(ReadOnlySpan<uint> leafNodeIndices)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (uint leafNodeIndex in leafNodeIndices)
            {
                if (_bvh.TryPageForLeaf(leafNodeIndex, out uint pageId))
                    _pages.Touch(pageId);
            }
        }
    }

    private static void ValidatePage(
        ReadOnlySpan<byte> page,
        in MeshPageHeader header,
        int payloadOffset)
    {
        int clusterBytes = checked((int)header.ClusterCount * GPUCluster.SizeInBytes);
        ReadOnlySpan<GPUCluster> clusters = MemoryMarshal.Cast<byte, GPUCluster>(
            page.Slice(checked((int)header.ClustersOffset), clusterBytes));
        int positionBytes = checked((int)header.TotalVertexCount * 3 * sizeof(ushort));
        ReadOnlySpan<ushort> positions = MemoryMarshal.Cast<byte, ushort>(
            page.Slice(checked((int)header.PositionsOffset), positionBytes));
        uint expectedVertexStart = 0;
        uint expectedIndexStart = 0;
        uint totalIndexCount = checked(header.TotalTriangleCount * 3);

        for (int index = 0; index < clusters.Length; index++)
        {
            ref readonly GPUCluster cluster = ref clusters[index];
            uint vertexCount = cluster.PackedCounts & 0xFF;
            uint triangleCount = (cluster.PackedCounts >> 8) & 0xFF;
            if (vertexCount == 0 || triangleCount == 0 || (cluster.PackedCounts & 0xFF000000) != 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} has invalid encoded counts in cluster {index}.");
            }
            if (cluster.VertexStart != expectedVertexStart || cluster.TriangleStart != expectedIndexStart)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} has non-canonical stream starts in cluster {index}: " +
                    $"vertex={cluster.VertexStart}, index={cluster.TriangleStart}.");
            }

            expectedVertexStart = checked(expectedVertexStart + vertexCount);
            expectedIndexStart = checked(expectedIndexStart + (triangleCount * 3));
            if (expectedVertexStart > header.TotalVertexCount || expectedIndexStart > totalIndexCount)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {index} exceeds its declared vertex or index stream.");
            }

            float lodError = (float)BitConverter.UInt16BitsToHalf(cluster.LODErrorHalf);
            if (!IsFinite(cluster.LODCenter) ||
                !float.IsFinite(cluster.LODRadius) || cluster.LODRadius < 0 ||
                !float.IsFinite(lodError) || lodError < 0 ||
                !IsFinite(cluster.BoundMin) || !IsFinite(cluster.BoundMax) ||
                cluster.BoundMin.X > cluster.BoundMax.X ||
                cluster.BoundMin.Y > cluster.BoundMax.Y ||
                cluster.BoundMin.Z > cluster.BoundMax.Z)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {index} has invalid bounds or LOD data.");
            }
            if (cluster.MaterialTableOffset != uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {index} requests an undefined slow material-table path.");
            }
            ValidateFastMaterialEncoding(cluster, triangleCount, payloadOffset, index);
            ValidateVrbEncoding(cluster.VRBBatchInfo, triangleCount, payloadOffset, index);
            ValidateDecodedCoordinates(cluster, positions, vertexCount, header, payloadOffset, index);

            int indicesStart = checked((int)header.IndicesOffset + cluster.TriangleStart);
            int indicesLength = checked((int)triangleCount * 3);
            foreach (byte vertexIndex in page.Slice(indicesStart, indicesLength))
            {
                if (vertexIndex >= vertexCount)
                {
                    throw new InvalidDataException(
                        $"Cluster page at byte {payloadOffset} cluster {index} contains vertex index {vertexIndex}, " +
                        $"but only {vertexCount} cluster-local vertices exist.");
                }
            }
        }

        if (expectedVertexStart != header.TotalVertexCount || expectedIndexStart != totalIndexCount)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} does not completely describe its vertex and index streams.");
        }
    }

    private static void ValidateStreamedPage(
        ReadOnlySpan<byte> page,
        in MeshPageReadSource expectedSource)
    {
        MeshPayloadPage descriptor = MeshPayloadLayout.ReadPage(page, 0, page.Length);
        if (descriptor.Size != page.Length)
        {
            throw new InvalidDataException(
                $"Streamed cluster page contains {page.Length} bytes, but its header declares {descriptor.Size}.");
        }
        if (!expectedSource.MatchesStreamedDescriptor(descriptor))
        {
            throw new InvalidDataException(
                "Streamed cluster page metadata does not match the page registered for this stable page id.");
        }

        MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
        ValidatePage(page, header, payloadOffset: 0);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void ValidateFastMaterialEncoding(
        in GPUCluster cluster,
        uint triangleCount,
        int payloadOffset,
        int clusterIndex)
    {
        uint range0End = cluster.PackedRanges & 0xFF;
        uint range1End = (cluster.PackedRanges >> 8) & 0xFF;
        if ((cluster.PackedMaterials & 0xFF000000) != 0 ||
            (cluster.PackedRanges & 0xFFFF0000) != 0 ||
            range0End > range1End ||
            range1End > triangleCount)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has a non-canonical fast material encoding.");
        }
    }

    private static void ValidateVrbEncoding(
        uint value,
        uint triangleCount,
        int payloadOffset,
        int clusterIndex)
    {
        uint encodedBatchCount = ((value >> 25) & 0x7) + 1;
        if ((value & 0xF0000000) != 0 || encodedBatchCount > 5)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has invalid VRB batch metadata.");
        }

        uint triangleSum = 0;
        for (uint batch = 0; batch < encodedBatchCount; batch++)
            triangleSum = checked(triangleSum + (((value >> checked((int)(batch * 5))) & 0x1F) + 1));
        for (uint batch = encodedBatchCount; batch < 5; batch++)
        {
            if (((value >> checked((int)(batch * 5))) & 0x1F) != 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} stores unused VRB batch bits.");
            }
        }

        if (triangleSum > triangleCount)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} VRB batches cover {triangleSum} of {triangleCount} triangles.");
        }
    }

    private static void ValidateDecodedCoordinates(
        in GPUCluster cluster,
        ReadOnlySpan<ushort> positions,
        uint vertexCount,
        in MeshPageHeader header,
        int payloadOffset,
        int clusterIndex)
    {
        int firstWord = checked((int)cluster.VertexStart * 3);
        for (int vertex = 0; vertex < checked((int)vertexCount); vertex++)
        {
            int word = checked(firstWord + (vertex * 3));
            ValidateDecodedCoordinate(cluster.IntBaseX, positions[word], header.QuantStep, header.QuantOriginX, payloadOffset, clusterIndex);
            ValidateDecodedCoordinate(cluster.IntBaseY, positions[word + 1], header.QuantStep, header.QuantOriginY, payloadOffset, clusterIndex);
            ValidateDecodedCoordinate(cluster.IntBaseZ, positions[word + 2], header.QuantStep, header.QuantOriginZ, payloadOffset, clusterIndex);
        }

        (ushort centerX, ushort centerY) = GPUCluster.UnpackU16(cluster.PackedCenterXY);
        (ushort centerZ, ushort radius) = GPUCluster.UnpackU16(cluster.PackedCenterZRadius);
        ValidateDecodedCoordinate(cluster.IntBaseX, centerX, header.QuantStep, header.QuantOriginX, payloadOffset, clusterIndex);
        ValidateDecodedCoordinate(cluster.IntBaseY, centerY, header.QuantStep, header.QuantOriginY, payloadOffset, clusterIndex);
        ValidateDecodedCoordinate(cluster.IntBaseZ, centerZ, header.QuantStep, header.QuantOriginZ, payloadOffset, clusterIndex);
        if (!float.IsFinite(radius * header.QuantStep))
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} decodes a non-finite quantized radius.");
        }
    }

    private static void ValidateDecodedCoordinate(
        int integerBase,
        ushort local,
        float quantStep,
        float quantOrigin,
        int payloadOffset,
        int clusterIndex)
    {
        long integer = (long)integerBase + local;
        if (integer is < int.MinValue or > int.MaxValue ||
            !float.IsFinite(((float)integer * quantStep) + quantOrigin))
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} decodes a non-finite or overflowing coordinate.");
        }
    }
    public bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pages.TryRegistration(
                    mesh,
                    out _,
                    out _,
                    out _))
            {
                // The registration owns a retained payload source. A Mesh reload publishes a
                // source for future epochs without invalidating readers of the current epoch.
            }
            return _bvh.TryPublishedRoot(mesh, out root);
        }
    }

    public int PublishedMeshCount
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _bvh.PublishedCount;
            }
        }
    }

    private bool TryEvictPage(uint protectedPageID)
    {
        return _pages.TryVictim(protectedPageID, out uint pageID)
            && EvictPage(pageID);
    }

    /// <summary>
    /// Publishes all pending residency changes after the execution owner has proved that every
    /// previous reader of the mapped Cluster resources has completed. The visibility switch and
    /// retirement commit are synchronous; no synthetic GPU submission participates in it.
    /// </summary>
    public bool PublishPending()
    {
        ResidencyReservation[] gpuToRelease;
        DirectPageBacking[] backingsToRelease;
        Exception? cleanupFailure = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            _bvh.PreparePublication();
            uint[] residentPages = _pages.PrepareLoads();
            PageRetirement[] retirements = _pages.PrepareEvictions();

            if (!_bvh.HasPendingPublication &&
                residentPages.Length == 0 &&
                retirements.Length == 0)
            {
                return false;
            }

            uint publishedEvictedPageCount = checked(
                _evictedPageCount + checked((uint)retirements.Length));
            _heap.ReserveFrees(retirements.Length);
            _quarantinedPageBackings.EnsureCapacity(checked(
                _quarantinedPageBackings.Count + retirements.Length));
            gpuToRelease = retirements.Length == 0
                ? []
                : new ResidencyReservation[retirements.Length];
            backingsToRelease = retirements.Length == 0
                ? []
                : new DirectPageBacking[retirements.Length];
            for (int index = 0; index < retirements.Length; index++)
            {
                uint pageId = retirements[index].PageID;
                if (!_directPageBackings.TryGetValue(pageId, out backingsToRelease[index]))
                    throw new InvalidOperationException($"Retiring Cluster page {pageId} has no final backing.");
            }

            // Every allocation and publication-state validation is complete before mapped
            // pointers or published CPU state change.
            _pages.ValidatePublication(residentPages, retirements);
            _heap.ValidateFrees(retirements);

            _pageStorage.Publish();
            _bvh.PublishPending();
            for (int index = 0; index < residentPages.Length; index++)
                _pages.PublishLoad(residentPages[index]);

            for (int index = 0; index < retirements.Length; index++)
            {
                PageRetirement retirement = retirements[index];
                gpuToRelease[index] = _pages.PublishEviction(retirement.PageID);
                DirectPageBacking backing = backingsToRelease[index];
                _directPageBackings.Remove(retirement.PageID);
                bool storageReleased = false;
                try
                {
                    // An offset becomes reusable only after its final-storage ownership is gone.
                    _pageStorage.Release(backing.Offset, checked((int)backing.Size));
                    storageReleased = true;
                    _heap.Free(retirement.Offset, retirement.Size);
                }
                catch (Exception error)
                {
                    // The page id is free to load into a different range. Any failed cleanup keeps
                    // the old offset allocated; an uncertain storage owner is also quarantined so
                    // epoch disposal can make one final release attempt.
                    if (!storageReleased)
                        _quarantinedPageBackings.Add(backing);
                    cleanupFailure ??= error;
                }
            }

            _evictedPageCount = publishedEvictedPageCount;
            AdvanceRevision();
        }

        Exception? reservationCleanupFailure = DisposePublicationResources(gpuToRelease);
        cleanupFailure ??= reservationCleanupFailure;
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.Publication, cleanupFailure);
        return true;
    }

    private static Exception? DisposePublicationResources(
        ReadOnlySpan<ResidencyReservation> gpuReservations,
        ReadOnlySpan<MeshPayloadSource?> sources = default)
    {
        Exception? firstFailure = null;
        DisposeAll(gpuReservations, ref firstFailure);
        foreach (MeshPayloadSource? source in sources)
        {
            try
            {
                source?.Dispose();
            }
            catch (Exception error)
            {
                firstFailure ??= error;
            }
        }
        return firstFailure;

        static void DisposeAll(
            ReadOnlySpan<ResidencyReservation> reservations,
            ref Exception? firstFailure)
        {
            foreach (ResidencyReservation reservation in reservations)
            {
                try
                {
                    reservation.Dispose();
                }
                catch (Exception error)
                {
                    firstFailure ??= error;
                }
            }
        }
    }

    private void RecordCleanupFailure(ClusterCleanupStage stage, Exception error)
    {
        lock (_gate)
        {
            if (_cleanupFailureSequence != ulong.MaxValue)
                _cleanupFailureSequence++;
            _lastCleanupFailure = new ClusterCleanupFailure(
                _cleanupFailureSequence,
                stage,
                error.GetType().FullName ?? error.GetType().Name,
                error.Message);
            AdvanceRevision();
            if (_disposed != 0 && _terminalSnapshot is ClusterMeshesSnapshot terminal)
            {
                _terminalSnapshot = terminal with
                {
                    ManagerStateRevision = _stateRevision,
                    LastCleanupFailure = _lastCleanupFailure,
                };
            }
        }
    }

    private ClusterMeshesSnapshot CreateSnapshot(ClusterLifecycle lifecycle)
    {
        return new ClusterMeshesSnapshot(
            EpochId,
            lifecycle,
            _stateRevision,
            new ClusterPageStateSnapshot(
                _pages.Count,
                _pages.ResidentCount,
                _pages.MissingCount,
                _pages.PendingLoadCount,
                _pages.PendingEvictionCount,
                _evictedPageCount),
            new ClusterHeapSnapshot(
                _heap.Capacity,
                _heap.UsedBytes,
                _heap.FreeBytes,
                _heap.Largest(),
                _heap.FreeBlockCount),
            _bvh.HasPendingPublication ||
                _pages.PendingLoadCount != 0 ||
                _pages.PendingEvictionCount != 0,
            new ClusterResidencySnapshot(
                _residency.Used(ResidencyClass.Gpu)),
            _bvh.RegisteredCount,
            _bvh.PublishedCount,
            _activePageStreams,
            _lastCleanupFailure);
    }

    private void AdvanceRevision()
    {
        if (_stateRevision != ulong.MaxValue)
            _stateRevision++;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    /// <summary>Destroys the whole append-only epoch after its owner has quiesced GPU readers.</summary>
    public void Dispose()
    {
        if (IsRegistrationOwner)
            throw new InvalidOperationException("A Cluster registration cannot dispose its own epoch.");
        StopRegistrations().GetAwaiter().GetResult();
        Exception? cleanupFailure = DisposeWithFailure();
        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException(
                "Cluster epoch disposal completed with cleanup failures.",
                cleanupFailure);
        }
    }

    internal Exception? DisposeWithFailure()
    {
        DirectPageBacking[] directBackings;
        PreparedMeshPagesDisposal pageDisposal;
        Exception? cleanupFailure;
        lock (_gate)
        {
            if (_disposed != 0)
                return null;
            if (_activePageStreams != 0)
            {
                throw new InvalidOperationException(
                    "A Cluster epoch cannot be disposed while a page stream still owns its lifecycle.");
            }
            if (_directLoads.Count != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while direct page IO is active.");
            if (_registrationOperationCount != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while mesh registration IO is active.");

            int directBackingCount = checked(
                _directPageBackings.Count + _quarantinedPageBackings.Count);
            directBackings = directBackingCount == 0
                ? []
                : new DirectPageBacking[directBackingCount];
            int backingIndex = 0;
            foreach (KeyValuePair<uint, DirectPageBacking> entry in _directPageBackings)
                directBackings[backingIndex++] = entry.Value;
            foreach (DirectPageBacking backing in _quarantinedPageBackings)
                directBackings[backingIndex++] = backing;
            pageDisposal = _pages.PrepareDisposal();
            ulong terminalRevision = _stateRevision == ulong.MaxValue
                ? ulong.MaxValue
                : _stateRevision + 1;
            var terminal = new ClusterMeshesSnapshot(
                EpochId,
                ClusterLifecycle.Disposed,
                terminalRevision,
                new ClusterPageStateSnapshot(
                    Registered: 0,
                    Resident: 0,
                    Missing: 0,
                    UncompletedLoads: 0,
                    UncompletedEvictions: 0,
                    TotalCompletedEvictions: _evictedPageCount),
                new ClusterHeapSnapshot(
                    _heap.Capacity,
                    UsedBytes: 0,
                    FreeBytes: _heap.Capacity,
                    LargestFreeBlockBytes: _heap.Capacity,
                    FreeBlockCount: 1),
                HasPendingPublication: false,
                Residency: default,
                RegisteredMeshCount: 0,
                PublishedMeshCount: 0,
                ActivePageStreams: 0,
                LastCleanupFailure: _lastCleanupFailure);

            // Everything above this line is preparation and may allocate or throw. The epoch
            // becomes disposed only after its immutable terminal observation is complete.
            AdvanceRevision();
            _terminalSnapshot = terminal;
            Volatile.Write(ref _disposed, 1);
            _directPageBackings.Clear();
            _quarantinedPageBackings.Clear();
            _pages.CommitDisposal(pageDisposal);
            cleanupFailure = _bvh.Clear();
            _heap.Reset();
        }

        Exception? resourceCleanupFailure = DisposePublicationResources(
            pageDisposal.GpuReservations,
            pageDisposal.OwnedSources);
        cleanupFailure ??= resourceCleanupFailure;
        foreach (DirectPageBacking backing in directBackings)
        {
            try
            {
                _pageStorage.Release(backing.Offset, checked((int)backing.Size));
            }
            catch (Exception error)
            {
                cleanupFailure ??= error;
            }
        }
        try
        {
            _pageStorage.Dispose();
        }
        catch (Exception error)
        {
            cleanupFailure ??= error;
        }
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.Disposal, cleanupFailure);
        _registrationAdmission.Dispose();
        _registrationShutdown.Dispose();
        return cleanupFailure;
    }
}


