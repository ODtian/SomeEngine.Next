using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.Collections;
using SomeEngine.Graphics;
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
        : base($"Reading Cluster page {pageId} into CPU staging storage failed.", innerException)
    {
    }
}

internal readonly record struct ClusterMeshRegistration(
    Mesh Mesh,
    uint FirstPageId,
    uint PageCount,
    uint RootNode,
    uint[] LowestDetailPageIds);

internal sealed record ClusterMeshPublication(
    ClusterBvhPublication Bvh,
    uint[] ResidentPages,
    PageRetirement[] Retirements,
    ClusterStagedUpload[] PageUploads,
    BufferRange[] ResidentPageRanges,
    ClusterLeafFlagFixup[] LeafFlagFixups,
    ClusterLodChange[] LodChanges);

internal sealed partial class ClusterMeshes : IDisposable
{
    private uint _maximumRasterBatchesPerCluster = 9;
    internal uint MaximumRasterBatchesPerCluster
    {
        get { lock (_gate) return _maximumRasterBatchesPerCluster; }
    }
    internal uint GeometryPageCount
    {
        get { lock (_gate) return _pages.Count; }
    }
    private static long s_nextEpochId;

    private readonly PageHeap _heap;
    private readonly object _gate = new();
    private readonly ClusterBvh _bvh;
    private readonly MeshPages _pages = new();
    private readonly ClusterPageFixups _fixups = new();
    private readonly HashSet<uint> _referencedPages = [];
    private readonly ResidencyBudgetLedger _residency;
    private readonly IClusterPageStorage _pageStorage;
    private readonly Dictionary<uint, StagedPageLoad> _stagingLoads = new();
    private readonly Dictionary<uint, PageAllocation> _pageAllocations = new();
    private readonly List<StagingBlock> _quarantinedStagingBlocks = [];
    private readonly List<PendingPageRetirement> _pendingPageRetirements = [];
    private ulong _stateRevision;
    private ulong _cleanupFailureSequence;
    private uint _evictedPageCount;
    private int _activePageStreams;
    private int _disposed;
    private ClusterCleanupFailure? _lastCleanupFailure;
    private ClusterMeshesSnapshot? _terminalSnapshot;
    private ClusterMeshPublication? _preparedPublication;

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

    private sealed class StagedPageLoad
    {
        internal StagedPageLoad(
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

    private readonly record struct PageAllocation(uint Offset, uint Size);
    private readonly record struct StagingBlock(uint Offset, uint Size);

    private readonly record struct PendingPageRetirement(
        PageAllocation Allocation,
        ResidencyReservation Reservation,
        QueueCompletion[] Completions);

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

    internal void ResolvePages(
        Dictionary<uint, uint> priorities,
        List<KeyValuePair<uint, uint>> unresolved)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            unresolved.Clear();
            foreach (KeyValuePair<uint, uint> request in priorities)
            {
                PageFaultResolution resolution = ResolvePageCore(request.Key);
                if (resolution.Kind is not (PageFaultResolutionKind.Satisfied or PageFaultResolutionKind.Pending))
                    unresolved.Add(request);
            }
        }
    }

    internal void ResolvePages(
        ReadOnlySpan<ClusterPageDemand> priorities,
        List<KeyValuePair<uint, uint>> unresolved)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            unresolved.Clear();
            foreach (ClusterPageDemand request in priorities)
            {
                PageFaultResolution resolution = ResolvePageCore(request.PageId);
                if (resolution.Kind is not (PageFaultResolutionKind.Satisfied or PageFaultResolutionKind.Pending))
                    unresolved.Add(new(request.PageId, request.Priority));
            }
        }
    }

    internal bool TryCollectPageDependencies(
        ReadOnlySpan<ClusterPageDemand> demands,
        List<ClusterPageDemand> expanded)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _fixups.TryExpandDependencies(demands, checked((int)_pages.Count), expanded);
        }
    }

    internal void CollectPageDependencies(HashSet<uint> pages)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _fixups.ExpandDependencies(pages);
        }
    }

    internal void CollectPageDependencies(Dictionary<uint, uint> priorities)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _fixups.ExpandDependencies(priorities);
        }
    }

    internal ClusterStreamingDemand SetReferencedPages(IEnumerable<uint> pages, bool complete)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _referencedPages.Clear();
            ulong bytes = 0;
            foreach (uint page in pages)
            {
                _referencedPages.Add(page);
                bytes += _pages.CalculateStreamingPageBytes(page);
            }
            return new(bytes, _pages.CalculateStreamingCapacityBytes(_heap.Capacity), complete);
        }
    }

    internal ClusterStreamingDemand SetReferencedPages(ReadOnlySpan<ClusterPageDemand> pages, bool complete)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _referencedPages.Clear();
            ulong bytes = 0;
            foreach (ClusterPageDemand page in pages)
            {
                _referencedPages.Add(page.PageId);
                bytes += _pages.CalculateStreamingPageBytes(page.PageId);
            }
            return new(bytes, _pages.CalculateStreamingCapacityBytes(_heap.Capacity), complete);
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
            if (IsPreparedRetirement(pageID))
                return new PageFaultResolution(PageFaultResolutionKind.Pending, pageID, size);
            ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
            if (!_pages.TryCancelPendingEviction(pageID, out uint offset))
                throw new InvalidOperationException("Pending page eviction could not be cancelled atomically.");
            try
            {
                _fixups.PageChanged(pageID);
            }
            catch
            {
                _bvh.RestoreChanges(bvhCheckpoint);
                if (_fixups.HasResidentDependents(pageID, PageAvailable) || !_pages.StageEviction(pageID, out _))
                    throw new InvalidOperationException("Cancelled page eviction could not be rolled back atomically.");
                throw;
            }
            AdvanceRevision();
            return new PageFaultResolution(PageFaultResolutionKind.Satisfied, pageID, size);
        }

        if (_stagingLoads.ContainsKey(pageID) || _pages.IsLoadPending(pageID))
            return new PageFaultResolution(PageFaultResolutionKind.Pending, pageID, size);

        return new PageFaultResolution(PageFaultResolutionKind.NeedsLoad, pageID, size);
    }

    private bool IsPreparedRetirement(uint pageID)
    {
        if (_preparedPublication is not { } publication)
            return false;
        foreach (ref readonly PageRetirement retirement in publication.Retirements.AsSpan())
            if (retirement.PageID == pageID)
                return true;
        return false;
    }

    /// <summary>
    /// Reserves an unpublished page-heap range, reads the page into CPU staging storage, validates
    /// the payload, and only then makes the range eligible for GPU publication.
    /// </summary>
    internal async ValueTask<PageLoadResult> LoadPageIntoStagingAsync(
        uint pageID,
        CancellationToken cancellationToken = default)
    {
        StagedPageLoad load;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pages.IsRetiring(pageID))
                return PageLoadResult.Deferred;
            if (_pages.IsResident(pageID) || _pages.IsLoadPending(pageID) || _stagingLoads.ContainsKey(pageID))
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
                _stagingLoads.EnsureCapacity(checked(_stagingLoads.Count + 1));
                _quarantinedStagingBlocks.EnsureCapacity(checked(
                    _quarantinedStagingBlocks.Count + 1));
                if (!_heap.TryAlloc(expectedSize, out uint offset))
                {
                    if (_pages.PendingEvictionCount == 0)
                        _ = TryEvictPage(pageID);
                    return PageLoadResult.Deferred;
                }
                bool storageAllocated = false;
                try
                {
                    uint allocationSize = PageHeap.AllocationSize(expectedSize);
                    Memory<byte> destination = _pageStorage.Allocate(offset, checked((int)allocationSize));
                    storageAllocated = true;
                    if (destination.Length != checked((int)allocationSize))
                    {
                        throw new InvalidOperationException(
                            $"Cluster page staging storage returned {destination.Length} bytes for a {allocationSize}-byte allocation.");
                    }
                    // Define the allocation's alignment bytes in the same copy as
                    // the payload. Contiguous resident allocations can then share
                    // one resource range instead of leaving thousands of tiny gaps.
                    destination.Span[checked((int)expectedSize)..].Clear();
                    load = new StagedPageLoad(offset, expectedSize,
                        destination[..checked((int)expectedSize)], reservation!, source);
                    _stagingLoads.Add(pageID, load);
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
                            _pageStorage.Release(offset, checked((int)PageHeap.AllocationSize(expectedSize)));
                        }
                        catch (Exception error)
                        {
                            // An uncertain staging owner keeps its heap range quarantined until
                            // epoch disposal. Reusing it could alias the unreleased staging block.
                            _quarantinedStagingBlocks.Add(new StagingBlock(offset, expectedSize));
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
            uint maximumRasterBatches = ValidateStreamedPage(load.Destination.Span, load.Source);

            lock (_gate)
            {
                ThrowIfDisposed();
                _maximumRasterBatchesPerCluster = Math.Max(_maximumRasterBatchesPerCluster, maximumRasterBatches);
                if (!_stagingLoads.TryGetValue(pageID, out StagedPageLoad? current) ||
                    !ReferenceEquals(current, load))
                {
                    throw new InvalidOperationException($"Cluster page {pageID} no longer owns its staging destination.");
                }

                ResidencyReservation gpuReservation = load.Reservation
                    ?? throw new InvalidOperationException($"Cluster page {pageID} staging destination was already committed.");
                _pageAllocations.EnsureCapacity(checked(_pageAllocations.Count + 1));
                ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
                bool staged = false;
                bool backingTracked = false;
                try
                {
                    _pages.StageResident(pageID, load.Offset, gpuReservation);
                    staged = true;
                    _pageStorage.Stage(load.Offset, checked((int)PageHeap.AllocationSize(load.Size)));
                    _pageAllocations.Add(pageID, new PageAllocation(load.Offset, load.Size));
                    backingTracked = true;
                    _fixups.PageChanged(pageID);
                    load.Reservation = null;
                    _stagingLoads.Remove(pageID);
                    AdvanceRevision();
                    return PageLoadResult.Staged;
                }
                catch
                {
                    _bvh.RestoreChanges(bvhCheckpoint);
                    if (backingTracked)
                        _pageAllocations.Remove(pageID);
                    if (staged)
                        _ = _pages.CancelStagedResident(pageID, gpuReservation);
                    throw;
                }
            }
        }
        catch
        {
            CancelStagedLoad(pageID, load);
            throw;
        }
    }

    private void CancelStagedLoad(uint pageID, StagedPageLoad expected)
    {
        ResidencyReservation? reservation = null;
        Exception? cleanupFailure = null;
        lock (_gate)
        {
            if (_stagingLoads.TryGetValue(pageID, out StagedPageLoad? current) && ReferenceEquals(current, expected))
            {
                _stagingLoads.Remove(pageID);
                try
                {
                    _pageStorage.Release(expected.Offset, checked((int)PageHeap.AllocationSize(expected.Size)));
                    _heap.Free(expected.Offset, expected.Size);
                }
                catch (Exception error)
                {
                    // A failed staging release must not make the offset reusable. The storage
                    // owner will still clear all remaining ranges at epoch disposal.
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
            if (_referencedPages.Contains(pageID) || _fixups.HasResidentDependents(pageID, PageAvailable))
                return false;
            return StagePageEvictions([pageID]);
        }
    }

    private bool StagePageEvictions(ReadOnlySpan<uint> pages)
    {
        ClusterBvhCheckpoint bvhCheckpoint = _bvh.CaptureChanges();
        int staged = 0;
        bool complete = false;
        try
        {
            while (staged < pages.Length)
            {
                if (!_pages.StageEviction(pages[staged], out _)) return false;
                staged++;
            }
            foreach (uint page in pages) _fixups.PageChanged(page);
            AdvanceRevision();
            complete = true;
            return true;
        }
        finally
        {
            if (!complete)
            {
                _bvh.RestoreChanges(bvhCheckpoint);
                for (int index = 0; index < staged; index++)
                    if (!_pages.TryCancelPendingEviction(pages[index], out _))
                        throw new InvalidOperationException("Pending page eviction could not be rolled back atomically.");
            }
        }
    }

    private static uint ValidatePage(
        ReadOnlySpan<byte> page,
        in MeshPageHeader header,
        int payloadOffset)
    {
        int clusterBytes = checked((int)header.ClusterCount * PackedCluster.SizeInBytes);
        ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
            page.Slice(checked((int)header.ClustersOffset), clusterBytes));
        uint expectedVertexBlobStart = 0;
        uint maximumRasterBatches = 1;
        uint expectedVertexCount = 0;
        uint expectedIndexStart = 0;
        uint totalIndexCount = checked(header.TotalTriangleCount * 3);

        for (int index = 0; index < clusters.Length; index++)
        {
            ref readonly PackedCluster cluster = ref clusters[index];
            uint vertexCount = cluster.PackedCounts & 0xFF;
            uint triangleCount = (cluster.PackedCounts >> 8) & 0xFF;
            if (vertexCount == 0 || triangleCount == 0 || cluster.VertexBlobSize == 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} has invalid encoded counts in cluster {index}.");
            }
            if (cluster.VertexBlobOffset != expectedVertexBlobStart ||
                cluster.TriangleStart * 3u != expectedIndexStart)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} has non-canonical stream starts in cluster {index}: " +
                    $"vertexBlob={cluster.VertexBlobOffset}, index={cluster.TriangleStart}.");
            }

            expectedVertexBlobStart = checked(expectedVertexBlobStart + cluster.VertexBlobSize);
            expectedVertexCount = checked(expectedVertexCount + vertexCount);
            expectedIndexStart = checked(expectedIndexStart + (triangleCount * 3));
            if (expectedVertexBlobStart > header.VertexBlobBytes ||
                expectedVertexCount > header.TotalVertexCount ||
                expectedIndexStart > totalIndexCount)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {index} exceeds its declared blob, vertex, or index stream.");
            }

            float lodError = (float)BitConverter.UInt16BitsToHalf(cluster.LODErrorHalf);
            if (!IsFinite(cluster.LODCenter) ||
                !float.IsFinite(cluster.LODRadius) || cluster.LODRadius < 0 ||
                float.IsNaN(lodError) || lodError < 0 ||
                !IsFinite(cluster.BoundMin) || !IsFinite(cluster.BoundMax) ||
                cluster.BoundMin.X > cluster.BoundMax.X ||
                cluster.BoundMin.Y > cluster.BoundMax.Y ||
                cluster.BoundMin.Z > cluster.BoundMax.Z)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {index} has invalid bounds or LOD data.");
            }
            ValidateExtensionRange(cluster, header, payloadOffset, index);
            if (cluster.MaterialTableOffset == uint.MaxValue)
                ValidateFastMaterialEncoding(cluster, triangleCount, payloadOffset, index);
            else
                ValidateMaterialTable(page, cluster, header, payloadOffset, index);
            maximumRasterBatches = Math.Max(maximumRasterBatches,
                ValidateVertReuseBatchEncoding(page, cluster, triangleCount, payloadOffset, index));

            int indicesStart = checked((int)(header.IndicesOffset + cluster.TriangleStart * 3u));
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

        if (expectedVertexBlobStart != header.VertexBlobBytes ||
            expectedVertexCount != header.TotalVertexCount ||
            expectedIndexStart != totalIndexCount)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} does not completely describe its blob, vertex, and index streams.");
        }
        return maximumRasterBatches;
    }

    private static void ValidateExtensionRange(
        in PackedCluster cluster,
        in MeshPageHeader header,
        int payloadOffset,
        int clusterIndex)
    {
        if (cluster.ExtensionSize == 0)
        {
            if (cluster.ExtensionOffset != uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has a non-canonical empty extension.");
            }
            return;
        }

        ulong extensionEnd = checked((ulong)header.ExtensionDataOffset + header.ExtensionDataSize);
        ulong clusterExtensionEnd = checked((ulong)cluster.ExtensionOffset + cluster.ExtensionSize);
        if (cluster.ExtensionOffset < header.ExtensionDataOffset || clusterExtensionEnd > extensionEnd)
        {
            throw new InvalidDataException(
                $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has an out-of-range extension.");
        }
    }

    private static uint ValidateStreamedPage(
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
        return ValidatePage(page, header, payloadOffset: 0);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void ValidateFastMaterialEncoding(
        in PackedCluster cluster,
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

    private static void ValidateMaterialTable(ReadOnlySpan<byte> page, in PackedCluster cluster,
        in MeshPageHeader header, int payloadOffset, int clusterIndex)
    {
        uint rangeCount = cluster.PackedRanges >> 16;
        ulong extensionEnd = (ulong)header.ExtensionDataOffset + header.ExtensionDataSize;
        if (rangeCount is < 4 or > 64 || cluster.MaterialTableOffset < header.ExtensionDataOffset ||
            (cluster.MaterialTableOffset & 3u) != 0 ||
            (ulong)cluster.MaterialTableOffset + rangeCount * sizeof(uint) > extensionEnd ||
            cluster.VertReuseBatchWord0 < header.ExtensionDataOffset || (cluster.VertReuseBatchWord0 & 3u) != 0 ||
            (ulong)cluster.VertReuseBatchWord0 + (ulong)cluster.VertReuseBatchWord2 * sizeof(uint) > extensionEnd ||
            cluster.VertReuseBatchWord1 != rangeCount || cluster.VertReuseBatchWord2 < Math.Max(4u, (rangeCount * 9u + 31u) / 32u))
            throw new InvalidDataException($"Cluster page at byte {payloadOffset} cluster {clusterIndex} has an invalid material table.");
        uint triangle = 0;
        for (int range = 0; range < rangeCount; range++)
        {
            uint value = cluster.GetMaterialRange(page, range);
            uint length = (value >> 8) & 0xFFu;
            if ((value & 0xFFu) != triangle || length == 0 || (value & 0xFF000000u) != 0)
                throw new InvalidDataException($"Cluster page at byte {payloadOffset} cluster {clusterIndex} has an invalid material range.");
            triangle += length;
        }
        if (triangle != ((cluster.PackedCounts >> 8) & 0xFFu))
            throw new InvalidDataException($"Cluster page at byte {payloadOffset} cluster {clusterIndex} has incomplete material ranges.");
    }

    private static uint ValidateVertReuseBatchEncoding(
        ReadOnlySpan<byte> page,
        in PackedCluster cluster,
        uint triangleCount,
        int payloadOffset,
        int clusterIndex)
    {
        int rangeCount = cluster.MaterialRangeCount;
        int countBits = Math.Max(rangeCount, 3) * 4;
        int totalBits = cluster.MaterialTableOffset == uint.MaxValue ? 128 : checked((int)cluster.VertReuseBatchWord2 * 32);

        uint triangleStart = 0;
        int flatBatchIndex = 0;
        for (int rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
        {
            uint batchCount = cluster.GetVertexReuseBits(page, rangeIndex * 4, 4);
            if (batchCount == 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has a material range without a vertex-reuse batch.");
            }

            uint range = cluster.GetMaterialRange(page, rangeIndex);
            uint rangeEnd = (range & 0xFFu) + ((range >> 8) & 0xFFu);
            for (uint batch = 0; batch < batchCount; batch++)
            {
                if (countBits + flatBatchIndex * 5 + 5 > totalBits)
                    throw new InvalidDataException($"Cluster page at byte {payloadOffset} cluster {clusterIndex} has truncated batch data.");
                uint batchTriangleCount = cluster.GetVertexReuseBits(
                    page,
                    countBits + flatBatchIndex * 5,
                    5) + 1u;
                triangleStart = checked(triangleStart + batchTriangleCount);
                if (triangleStart > rangeEnd)
                {
                    throw new InvalidDataException(
                        $"Cluster page at byte {payloadOffset} cluster {clusterIndex} has a vertex-reuse batch outside its material range.");
                }
                flatBatchIndex++;
            }

            if (triangleStart != rangeEnd)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} vertex-reuse batches do not exactly cover their material range.");
            }
        }

        for (int rangeIndex = rangeCount; rangeIndex < 3; rangeIndex++)
        {
            if (cluster.GetVertexReuseBits(page, rangeIndex * 4, 4) != 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} stores batch counts for an unused material range.");
            }
        }

        int usedBits = countBits + flatBatchIndex * 5;
        for (int bit = usedBits; bit < totalBits; bit++)
        {
            if (cluster.GetVertexReuseBits(page, bit, 1) != 0)
            {
                throw new InvalidDataException(
                    $"Cluster page at byte {payloadOffset} cluster {clusterIndex} stores unused vertex-reuse batch bits.");
            }
        }
        return checked((uint)flatBatchIndex);
    }

    public bool TryGetPublishedRoot(Mesh mesh, out uint root)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _bvh.TryPublishedRoot(mesh, out root);
        }
    }

    internal bool TryGetRegisteredRoot(Mesh mesh, out uint root)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _bvh.TryRegisteredRoot(mesh, out root);
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
        if (_pages.TryVictim(protectedPageID, page => !_referencedPages.Contains(page) &&
            !_fixups.HasResidentDependents(page, PageAvailable), out uint pageID))
            return EvictPage(pageID);

        // A cold parent cycle has no individually removable page. Retire its
        // available members together, after checking the same view/root protection
        // and every resident child outside the component. The normal publication
        // applies all fixups before any allocation becomes reusable.
        var examined = new HashSet<ClusterPageFixups.PageDependency>();
        var pages = new List<uint>();
        ClusterPageFixups.PageDependency? protectedDependency = _fixups.FindPageDependency(protectedPageID);
        return _pages.TryVictim(protectedPageID, CanEvictDependency, out _) &&
            StagePageEvictions(CollectionsMarshal.AsSpan(pages));

        bool CanEvictDependency(uint page)
        {
            if (_referencedPages.Contains(page) || _fixups.FindPageDependency(page) is not { } dependency ||
                dependency == protectedDependency || !examined.Add(dependency)) return false;
            pages.Clear();
            foreach ((uint first, uint end) in dependency.Ranges)
                for (uint member = first; member < end; member++)
                {
                    if (!PageAvailable(member)) continue;
                    if (_referencedPages.Contains(member) || !_pages.CanEvictPage(member) ||
                        _fixups.HasResidentDependents(member, PageAvailable, dependency)) return false;
                    pages.Add(member);
                }
            return pages.Count != 0;
        }
    }

    /// <summary>
    /// Freezes the exact CPU staging uploads and effective resident intervals for one GPU frame.
    /// No published CPU state changes until that frame has submitted successfully.
    /// </summary>
    public ClusterMeshPublication? PreparePublication()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_preparedPublication is not null)
                return null;
            bool hasPendingBvh = _bvh.HasPendingPublication;
            uint[] residentPages = _pages.PrepareLoads();
            PageRetirement[] retirements = _pages.PrepareEvictions();

            if (!hasPendingBvh && residentPages.Length == 0 && retirements.Length == 0)
                return null;

            _pages.ValidatePublication(residentPages, retirements);
            _heap.ValidateFrees(retirements);
            for (int index = 0; index < retirements.Length; index++)
            {
                uint pageId = retirements[index].PageID;
                if (!_pageAllocations.ContainsKey(pageId))
                    throw new InvalidOperationException($"Retiring Cluster page {pageId} has no GPU allocation.");
            }
            _pendingPageRetirements.EnsureCapacity(checked(
                _pendingPageRetirements.Count + retirements.Length));
            _heap.ReserveFrees(retirements.Length);
            var fixups = _fixups.Apply(PageAvailable, PageOffset, _bvh);
            ClusterBvhPublication bvh = _bvh.PreparePublication();

            _preparedPublication = new ClusterMeshPublication(
                bvh,
                residentPages,
                retirements,
                _pageStorage.CaptureStagedUploads(),
                BuildResidentPageRanges(residentPages, retirements), fixups.Flags, fixups.Changes);
            _fixups.Accept(fixups);
            return _preparedPublication;
        }
    }

    /// <summary>Commits one submitted GPU publication and quarantines evicted ranges by completion.</summary>
    public void CommitPublication(
        ClusterMeshPublication publication,
        QueueCompletion[][] retirementCompletions)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(retirementCompletions);
        if (retirementCompletions.Length != publication.Retirements.Length)
            throw new ArgumentException("Every page retirement requires one completion set.", nameof(retirementCompletions));

        Exception? cleanupFailure = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(publication, _preparedPublication))
                throw new InvalidOperationException("The prepared Cluster publication is stale.");
            _pages.ValidatePublication(publication.ResidentPages, publication.Retirements);
            _heap.ValidateFrees(publication.Retirements);
            _pageStorage.CompleteStagedUploads(publication.PageUploads);
            _bvh.CommitPublication(publication.Bvh);
            foreach (uint pageId in publication.ResidentPages)
                _pages.PublishLoad(pageId);

            for (int index = 0; index < publication.Retirements.Length; index++)
            {
                PageRetirement retirement = publication.Retirements[index];
                if (!_pageAllocations.Remove(retirement.PageID, out PageAllocation allocation))
                    throw new InvalidOperationException($"Retiring Cluster page {retirement.PageID} has no GPU allocation.");
                ResidencyReservation reservation = _pages.PublishEviction(retirement.PageID);
                QueueCompletion[] completions = retirementCompletions[index];
                if (completions.Length != 0)
                {
                    _pendingPageRetirements.Add(new PendingPageRetirement(
                        allocation,
                        reservation,
                        completions));
                    continue;
                }
                try
                {
                    _heap.Free(allocation.Offset, allocation.Size);
                }
                catch (Exception error)
                {
                    cleanupFailure ??= error;
                }
                try
                {
                    reservation.Dispose();
                }
                catch (Exception error)
                {
                    cleanupFailure ??= error;
                }
            }

            _residentPageRanges = publication.ResidentPageRanges;
            _evictedPageCount = checked(
                _evictedPageCount + checked((uint)publication.Retirements.Length));
            _preparedPublication = null;
            AdvanceRevision();
        }
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.Publication, cleanupFailure);
    }

    /// <summary>Returns only ranges whose prior readers have completed to the page allocator.</summary>
    internal void RetireCompleted(IGraphicsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        RetireCompletedCore(backend);
    }

    private void RetireCompletedCore(IGraphicsBackend backend)
    {
        List<ResidencyReservation>? reservations = null;
        Exception? cleanupFailure = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            for (int index = _pendingPageRetirements.Count - 1; index >= 0; index--)
            {
                PendingPageRetirement pending = _pendingPageRetirements[index];
                bool complete = true;
                foreach (QueueCompletion completion in pending.Completions)
                {
                    if (backend.IsComplete(completion))
                        continue;
                    complete = false;
                    break;
                }
                if (!complete)
                    continue;

                _pendingPageRetirements.RemoveAt(index);
                try
                {
                    _heap.Free(pending.Allocation.Offset, pending.Allocation.Size);
                }
                catch (Exception error)
                {
                    cleanupFailure ??= error;
                }
                (reservations ??= []).Add(pending.Reservation);
                AdvanceRevision();
            }
        }

        if (reservations is not null)
        {
            Exception? reservationFailure = DisposePublicationResources(
                CollectionsMarshal.AsSpan(reservations));
            cleanupFailure ??= reservationFailure;
        }
        if (cleanupFailure is not null)
            RecordCleanupFailure(ClusterCleanupStage.Publication, cleanupFailure);
    }

    private bool PageAvailable(uint page) => !_pages.IsRetiring(page) &&
        (_pages.IsResident(page) || _pages.IsLoadPending(page));

    private uint PageOffset(uint page)
    {
        if (!_pageAllocations.TryGetValue(page, out PageAllocation allocation))
            throw new InvalidOperationException("A geometry fixup requires a resident page allocation.");
        return allocation.Offset;
    }

    private BufferRange[] _residentPageRanges = [];

    internal BufferRange[] CaptureResidentPageRanges()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _residentPageRanges;
        }
    }

    internal int HierarchyLevels
    {
        get { lock (_gate) { ThrowIfDisposed(); return _bvh.HierarchyLevels; } }
    }

    internal BufferRange CaptureBvhRange()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ulong length = _bvh.ByteLength;
            return length == 0 ? default : new BufferRange(0, length);
        }
    }

    private BufferRange[] BuildResidentPageRanges(
        ReadOnlySpan<uint> publishingLoads,
        ReadOnlySpan<PageRetirement> publishingEvictions)
    {
        var ranges = new List<BufferRange>(_pageAllocations.Count);
        foreach ((uint pageId, PageAllocation backing) in _pageAllocations)
        {
            bool loading = publishingLoads.BinarySearch(pageId) >= 0;
            bool evicting = false;
            foreach (ref readonly PageRetirement retirement in publishingEvictions)
            {
                if (retirement.PageID != pageId)
                    continue;
                evicting = true;
                break;
            }
            if ((!_pages.IsResident(pageId) && !loading) || evicting)
                continue;
            ranges.Add(new BufferRange(backing.Offset, PageHeap.AllocationSize(backing.Size)));
        }
        if (ranges.Count == 0)
            return [];
        ranges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        var merged = new List<BufferRange>(ranges.Count);
        BufferRange current = ranges[0];
        for (int index = 1; index < ranges.Count; index++)
        {
            BufferRange next = ranges[index];
            ulong currentEnd = checked(current.Offset + current.Size);
            if (next.Offset > currentEnd)
            {
                merged.Add(current);
                current = next;
                continue;
            }
            ulong end = Math.Max(currentEnd, checked(next.Offset + next.Size));
            current = new BufferRange(current.Offset, end - current.Offset);
        }
        merged.Add(current);
        return [.. merged];
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
                checked(_pages.PendingEvictionCount + _pendingPageRetirements.Count),
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
        StagingBlock[] quarantinedStagingBlocks;
        PendingPageRetirement[] pendingRetirements;
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
            if (_stagingLoads.Count != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while staged page IO is active.");
            if (_registrationOperationCount != 0)
                throw new InvalidOperationException("A Cluster epoch cannot be disposed while mesh registration IO is active.");

            quarantinedStagingBlocks = [.. _quarantinedStagingBlocks];
            pendingRetirements = [.. _pendingPageRetirements];
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
            _pageAllocations.Clear();
            _quarantinedStagingBlocks.Clear();
            _pendingPageRetirements.Clear();
            _preparedPublication = null;
            _pages.CommitDisposal(pageDisposal);
            cleanupFailure = _bvh.Clear();
            _fixups.Clear();
            _referencedPages.Clear();
            _heap.Reset();
        }

        Exception? resourceCleanupFailure = DisposePublicationResources(
            pageDisposal.GpuReservations,
            pageDisposal.OwnedSources);
        cleanupFailure = cleanupFailure ?? resourceCleanupFailure;
        foreach (PendingPageRetirement pending in pendingRetirements)
        {
            try
            {
                pending.Reservation.Dispose();
            }
            catch (Exception error)
            {
                cleanupFailure ??= error;
            }
        }
        foreach (StagingBlock block in quarantinedStagingBlocks)
        {
            try
            {
                _pageStorage.Release(block.Offset, checked((int)PageHeap.AllocationSize(block.Size)));
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


