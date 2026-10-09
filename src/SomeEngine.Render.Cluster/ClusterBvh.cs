using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Core.Collections;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterMeshRoot(Mesh Mesh, uint NodeIndex);
internal readonly record struct ClusterRootState(uint NodeIndex, bool Published);

internal readonly record struct ClusterBvhRegistration(
    Mesh Mesh,
    uint FirstNode,
    uint FirstPage,
    uint Root,
    int ByteLength,
    uint NodeCount,
    int PageCount,
    uint[] LeafReferences,
    Memory<byte> Nodes,
    int HierarchyLevels);

internal readonly record struct ClusterBvhLeafIndex(
    Mesh Mesh,
    uint FirstNode,
    uint NodeCount,
    uint FirstPage,
    uint[] LeafReferences,
    Memory<byte> Nodes);

internal readonly record struct ClusterBvhDestination(ulong Offset, Memory<byte> Memory);
internal readonly record struct ClusterBvhAllocation(ulong Offset, uint Length);
internal readonly record struct ClusterBvhPatch(
    ulong Offset,
    uint Value,
    Memory<byte> Destination);
internal readonly record struct ClusterBvhCheckpoint(int PatchCount);
internal sealed record ClusterBvhPublication(
    int PatchCount,
    Mesh[] RootMeshes,
    Mesh[] ChangedMeshes,
    ClusterStagedUpload[] Uploads);

internal sealed class ClusterBvh
{
    internal const int TraversalStackCapacity = 128;

    internal const ulong DefaultCapacityBytes = 64UL * 1024 * 1024;
    internal const uint PageFaultMarker = uint.MaxValue;
    internal static readonly uint NodeBytes = checked((uint)Unsafe.SizeOf<ClusterBVHNode>());
    private const uint ChildReferenceByteOffset = 140;

    private readonly IClusterBvhStorage _storage;
    private readonly List<ClusterBvhAllocation> _allocations = [];
    private readonly List<ClusterBvhPatch> _pendingPatches = [];
    private readonly FlatDictionary<Mesh, ClusterRootState> _roots = new();
    private readonly List<ClusterBvhLeafIndex> _leafIndexes = [];
    private readonly List<Mesh> _pendingRootMeshes = [];
    private uint _count;
    private uint _registeredPageCount;
    private int _publishedCount;

    internal ClusterBvh(IClusterBvhStorage? storage = null)
        => _storage = storage ?? new SparseClusterBvhStorage(DefaultCapacityBytes);

    public int HierarchyLevels { get; private set; } = 1;

    public int RegisteredCount => _roots.Count;
    public int PublishedCount => _publishedCount;
    public ulong ByteLength => checked((ulong)_count * NodeBytes);
    public bool HasPendingPublication
        => _pendingPatches.Count != 0 ||
           _pendingRootMeshes.Count != 0;

    public bool IsRegistered(Mesh mesh)
        => _roots.ContainsKey(mesh);

    public bool TryRegisteredRoot(Mesh mesh, out uint root)
    {
        if (_roots.TryGetValue(mesh, out ClusterRootState state))
        {
            root = state.NodeIndex;
            return true;
        }
        root = 0;
        return false;
    }

    public bool TryPublishedRoot(Mesh mesh, out uint root)
    {
        if (_roots.TryGetValue(mesh, out ClusterRootState state) && state.Published)
        {
            root = state.NodeIndex;
            return true;
        }
        root = 0;
        return false;
    }

    public bool TryPageForLeaf(uint nodeIndex, out uint pageId)
    {
        uint child = nodeIndex & 3;
        nodeIndex >>= 2;
        if (TryFindLeafIndexByNode(nodeIndex, out ClusterBvhLeafIndex index))
        {
            uint reference = index.LeafReferences[checked((int)((nodeIndex - index.FirstNode) * 4 + child))];
            uint localPage = reference == uint.MaxValue ? uint.MaxValue : reference >> 8;
            if (localPage != uint.MaxValue)
            {
                pageId = checked(index.FirstPage + localPage);
                return true;
            }
        }

        pageId = 0;
        return false;
    }

    public ClusterBvhDestination AllocateRegistration(int byteLength)
    {
        if (byteLength <= 0 || byteLength % checked((int)NodeBytes) != 0)
            throw new InvalidDataException($"Cluster BVH length must be a positive multiple of {NodeBytes} bytes.");
        uint nodeCount = checked((uint)(byteLength / checked((int)NodeBytes)));
        if (nodeCount > uint.MaxValue - _count)
            throw new InvalidOperationException("Global Cluster BVH node index space is exhausted.");
        _allocations.EnsureCapacity(checked(_allocations.Count + 1));
        ulong offset = checked((ulong)_count * NodeBytes);
        return new ClusterBvhDestination(offset, _storage.Allocate(offset, byteLength));
    }

    public Exception? CancelRegistration(in ClusterBvhDestination destination)
    {
        try
        {
            _storage.Release(destination.Offset, destination.Memory.Length);
            return null;
        }
        catch (Exception error)
        {
            // A backing that could not be released must never be reused. Keep it in the epoch's
            // allocation ledger so disposal retries the release, and advance past the quarantined
            // node range before another registration can allocate.
            uint byteLength = checked((uint)destination.Memory.Length);
            uint nodeCount = checked(byteLength / NodeBytes);
            _allocations.Add(new ClusterBvhAllocation(destination.Offset, byteLength));
            _count = checked(_count + nodeCount);
            return error;
        }
    }

    public ClusterBvhRegistration Prepare(
        Mesh mesh,
        in ClusterBvhDestination destination,
        uint firstPage,
        IReadOnlyList<MeshPayloadPage> pages)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (_roots.ContainsKey(mesh))
            throw new InvalidOperationException($"Mesh '{mesh}' already has a registered Cluster BVH.");
        if (destination.Memory.IsEmpty || destination.Memory.Length % checked((int)NodeBytes) != 0)
            throw new InvalidDataException(
                $"Cluster BVH data must contain a non-empty whole number of {NodeBytes}-byte nodes.");

        int nodeCount = destination.Memory.Length / checked((int)NodeBytes);

        uint firstNode = _count;
        if (destination.Offset != checked((ulong)firstNode * NodeBytes))
            throw new InvalidOperationException("Cluster BVH destination is stale.");
        uint nodeCount32 = checked((uint)nodeCount);
        if (nodeCount32 > uint.MaxValue - firstNode)
        {
            throw new InvalidOperationException(
                $"Global BVH node index space exceeded: current={firstNode}, incoming={nodeCount}.");
        }

        Span<ClusterBVHNode> patched = MemoryMarshal.Cast<byte, ClusterBVHNode>(destination.Memory.Span);
        int hierarchyDepth = ValidateTopology(patched);
        uint[] localPageByNode = PatchNodes(patched, firstNode, firstPage, pages);
        uint root = firstNode;
        return new ClusterBvhRegistration(
            mesh,
            firstNode,
            firstPage,
            root,
            destination.Memory.Length,
            nodeCount32,
            pages.Count,
            localPageByNode,
            destination.Memory,
            hierarchyDepth);
    }

    public uint Commit(in ClusterBvhRegistration registration)
    {
        ulong byteOffset = checked((ulong)registration.FirstNode * NodeBytes);
        _storage.Stage(byteOffset, registration.ByteLength);
        var range = new ClusterBvhAllocation(byteOffset, checked((uint)registration.ByteLength));
        _allocations.Add(range);
        _count += registration.NodeCount;
        HierarchyLevels = Math.Max(HierarchyLevels, registration.HierarchyLevels);
        _roots.Add(registration.Mesh, new ClusterRootState(registration.Root, Published: false));
        _leafIndexes.Add(new ClusterBvhLeafIndex(
            registration.Mesh,
            registration.FirstNode,
            registration.NodeCount,
            registration.FirstPage,
            registration.LeafReferences,
            registration.Nodes));
        _registeredPageCount = checked(
            registration.FirstPage + checked((uint)registration.PageCount));
        _pendingRootMeshes.Add(registration.Mesh);
        return registration.Root;
    }

    public void ReserveCommit(in ClusterBvhRegistration registration)
    {
        if (registration.FirstNode != _count ||
            registration.FirstPage != _registeredPageCount ||
            registration.NodeCount == 0 ||
            registration.ByteLength != checked((int)(registration.NodeCount * NodeBytes)) ||
            registration.PageCount <= 0 ||
            registration.LeafReferences is null ||
            registration.LeafReferences.Length != checked((int)registration.NodeCount * 4) ||
            _roots.ContainsKey(registration.Mesh))
        {
            throw new InvalidOperationException("The prepared Cluster BVH registration is stale or malformed.");
        }

        _allocations.EnsureCapacity(checked(_allocations.Count + 1));
        _roots.EnsureCapacity(checked(_roots.Count + 1));
        _leafIndexes.EnsureCapacity(checked(_leafIndexes.Count + 1));
        _pendingRootMeshes.EnsureCapacity(checked(_pendingRootMeshes.Count + 1));
    }

    internal void PatchPart(uint node, uint child, uint reference)
        => PatchPartWord(node, child, ChildReferenceByteOffset, reference);

    internal void PatchPartErrors(uint node, uint child, uint errors)
        => PatchPartWord(node, child, 76, errors);

    private void PatchPartWord(uint node, uint child, uint wordOffset, uint value)
    {
        if (!TryFindLeafIndexByNode(node, out ClusterBvhLeafIndex mesh))
            throw new InvalidOperationException("A hierarchy fixup references an unknown mesh node.");
        int localNode = checked((int)(node - mesh.FirstNode));
        int referenceOffset = checked(localNode * (int)NodeBytes + (int)wordOffset + (int)child * 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(mesh.Nodes.Span.Slice(referenceOffset, sizeof(uint))) == value) return;
        ulong offset = checked((ulong)mesh.FirstNode * NodeBytes + (uint)referenceOffset);
        _pendingPatches.Add(new ClusterBvhPatch(offset, value, mesh.Nodes.Slice(referenceOffset, sizeof(uint))));
    }

    public ClusterBvhCheckpoint CaptureChanges()
        => new(_pendingPatches.Count);

    public void RestoreChanges(in ClusterBvhCheckpoint checkpoint)
    {
        if (checkpoint.PatchCount > _pendingPatches.Count)
            throw new InvalidOperationException("Cluster BVH checkpoint is ahead of pending metadata.");
        _pendingPatches.RemoveRange(
            checkpoint.PatchCount,
            _pendingPatches.Count - checkpoint.PatchCount);
    }

    public ClusterBvhPublication PreparePublication()
    {
        var changedMeshes = new HashSet<uint>();
        foreach (ClusterBvhPatch patch in _pendingPatches)
        {
            if (patch.Destination.Length != sizeof(uint))
                throw new InvalidOperationException($"Cluster BVH patch at {patch.Offset} has an invalid destination.");
            BinaryPrimitives.WriteUInt32LittleEndian(
                patch.Destination.Span,
                patch.Value);
            _storage.Stage(patch.Offset / NodeBytes * NodeBytes, checked((int)NodeBytes));
            uint node = checked((uint)(patch.Offset / NodeBytes));
            if (TryFindLeafIndexByNode(node, out ClusterBvhLeafIndex mesh))
                changedMeshes.Add(mesh.FirstNode);
        }
        foreach (Mesh mesh in _pendingRootMeshes)
        {
            if (!_roots.TryGetValue(mesh, out ClusterRootState root) || root.Published)
            {
                throw new InvalidOperationException("Pending Cluster root metadata is inconsistent.");
            }
        }
        return new ClusterBvhPublication(
            _pendingPatches.Count,
            [.. _pendingRootMeshes],
            _leafIndexes.Where(mesh => changedMeshes.Contains(mesh.FirstNode) ||
                _pendingRootMeshes.Contains(mesh.Mesh)).Select(static mesh => mesh.Mesh).ToArray(),
            _storage.CaptureStagedUploads());
    }

    public void CommitPublication(ClusterBvhPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (publication.PatchCount > _pendingPatches.Count ||
            publication.RootMeshes.Length > _pendingRootMeshes.Count)
        {
            throw new InvalidOperationException("The prepared Cluster BVH publication is ahead of pending state.");
        }
        for (int index = 0; index < publication.RootMeshes.Length; index++)
        {
            Mesh mesh = publication.RootMeshes[index];
            if (!ReferenceEquals(mesh, _pendingRootMeshes[index]))
                throw new InvalidOperationException("The prepared Cluster BVH root publication is stale.");
            ClusterRootState root = _roots[mesh];
            if (root.Published)
                throw new InvalidOperationException("The prepared Cluster BVH root is already published.");
            _roots[mesh] = root with { Published = true };
            _publishedCount = checked(_publishedCount + 1);
        }

        _storage.CompleteStagedUploads(publication.Uploads);
        _pendingPatches.RemoveRange(0, publication.PatchCount);
        _pendingRootMeshes.RemoveRange(0, publication.RootMeshes.Length);
    }

    public Exception? Clear()
    {
        Exception? firstFailure = null;
        foreach (ClusterBvhAllocation allocation in _allocations)
        {
            try
            {
                _storage.Release(allocation.Offset, checked((int)allocation.Length));
            }
            catch (Exception error)
            {
                firstFailure ??= error;
            }
        }
        _allocations.Clear();
        _pendingPatches.Clear();
        _roots.Clear();
        _leafIndexes.Clear();
        _pendingRootMeshes.Clear();
        _count = 0;
        HierarchyLevels = 1;
        _registeredPageCount = 0;
        _publishedCount = 0;
        try
        {
            _storage.Dispose();
        }
        catch (Exception error)
        {
            firstFailure ??= error;
        }
        return firstFailure;
    }

    private static uint[] PatchNodes(Span<ClusterBVHNode> nodes, uint firstNode, uint firstPage,
        IReadOnlyList<MeshPayloadPage> pages)
    {
        var references = Enumerable.Repeat(uint.MaxValue, nodes.Length * 4).ToArray();
        var coverage = pages.Select(page => new bool[page.ClusterCount]).ToArray();
        for (int index = 0; index < nodes.Length; index++)
        {
            for (int child = 0; child < 4; child++)
            {
                ClusterHierarchySlice slice = nodes[index].GetChild(child);
                if (!slice.IsEnabled) continue;
                if (!slice.IsLeaf)
                {
                    slice.ChildReference = checked(slice.ChildReference + firstNode);
                }
                else
                {
                    uint page = slice.ChildReference >> 8;
                    uint start = slice.FirstCluster, count = slice.ClusterCount;
                    if (page >= pages.Count || start + count > pages[(int)page].ClusterCount || count == 0)
                        throw new InvalidDataException("A hierarchy part exceeds its geometry page.");
                    for (uint cluster = start; cluster < start + count; cluster++)
                    {
                        if (coverage[page][cluster]) throw new InvalidDataException("Hierarchy parts overlap.");
                        coverage[page][cluster] = true;
                    }
                    references[index * 4 + child] = slice.ChildReference;
                    uint first = checked(slice.FirstPage + firstPage);
                    if (first + slice.PageCount > 0x2000000u)
                        throw new InvalidDataException("Geometry page ranges exceed their 25-bit representation.");
                    slice.PageRangeKey = (first << 5) | slice.PageCount | (slice.PageRangeKey & 0x40000000u);
                    slice.ChildReference = PageFaultMarker;
                }
                nodes[index].SetChild(child, slice);
            }
        }
        foreach (bool[] page in coverage)
            if (page.Contains(false)) throw new InvalidDataException("Hierarchy parts leave geometry uncovered.");
        return references;
    }

    private bool TryFindLeafIndexByNode(uint nodeIndex, out ClusterBvhLeafIndex result)
    {
        int low = 0;
        int high = _leafIndexes.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ClusterBvhLeafIndex candidate = _leafIndexes[middle];
            if (nodeIndex < candidate.FirstNode)
            {
                high = middle - 1;
                continue;
            }
            if (nodeIndex - candidate.FirstNode >= candidate.NodeCount)
            {
                low = middle + 1;
                continue;
            }

            result = candidate;
            return true;
        }

        result = default;
        return false;
    }

    private static int ValidateTopology(ReadOnlySpan<ClusterBVHNode> nodes)
    {
        if (nodes.Length == 0) throw new InvalidDataException("A cluster hierarchy requires a root.");
        ClusterBVHNode[] data = nodes.ToArray();
        var states = new byte[data.Length];
        int maximumDepth = 0;
        Visit(0, 1);
        if (states.Contains((byte)0)) throw new InvalidDataException("The hierarchy contains unreachable nodes.");
        return maximumDepth;

        void Visit(int index, int depth)
        {
            if ((uint)index >= data.Length || states[index] == 1)
                throw new InvalidDataException("The hierarchy contains an invalid child reference or cycle.");
            if (states[index] == 2) return;
            states[index] = 1;
            maximumDepth = Math.Max(maximumDepth, depth);
            for (int child = 0; child < 4; child++)
            {
                ClusterHierarchySlice slice = data[index].GetChild(child);
                if (!slice.IsEnabled) continue;
                if (!IsFinite(slice.BoundsCenter) || !IsFinite(slice.BoundsExtent) ||
                    slice.BoundsExtent.X < 0 || slice.BoundsExtent.Y < 0 || slice.BoundsExtent.Z < 0 ||
                    !IsFinite(new System.Numerics.Vector3(slice.LODBounds.X, slice.LODBounds.Y, slice.LODBounds.Z)) ||
                    !float.IsFinite(slice.LODBounds.W) || slice.LODBounds.W < 0 ||
                    float.IsNaN(slice.MinLODError) || slice.MinLODError < 0 ||
                    float.IsNaN(slice.MaxParentLODError) || slice.MaxParentLODError < 0)
                    throw new InvalidDataException("The hierarchy contains invalid bounds or errors.");
                if (!slice.IsLeaf) Visit(checked((int)slice.ChildReference), depth + 1);
            }
            states[index] = 2;
        }
    }

    private static bool IsFinite(System.Numerics.Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
