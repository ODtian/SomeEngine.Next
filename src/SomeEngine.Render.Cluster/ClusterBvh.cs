using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterMeshRoot(AssetHandle<Mesh> Mesh, uint NodeIndex);
internal readonly record struct ClusterRootState(uint NodeIndex, bool Published);

internal readonly record struct ClusterBvhRegistration(
    AssetHandle<Mesh> Mesh,
    uint FirstNode,
    uint FirstPage,
    uint Root,
    int ByteLength,
    uint NodeCount,
    int PageCount,
    uint[] LocalPageByNode);

internal readonly record struct ClusterBvhLeafIndex(
    uint FirstNode,
    uint NodeCount,
    uint FirstPage,
    int PageCount,
    uint[] LocalPageByNode);

internal readonly record struct ClusterBvhDestination(ulong Offset, Memory<byte> Memory);
internal readonly record struct ClusterBvhAllocation(ulong Offset, uint Length);
internal readonly record struct ClusterBvhPatch(
    ulong Offset,
    uint Value,
    Memory<byte> Destination);
internal readonly record struct ClusterBvhCheckpoint(int PatchCount);

internal sealed class ClusterBvh
{
    internal const int TraversalStackCapacity = 128;

    internal const ulong DefaultCapacityBytes = 64UL * 1024 * 1024;
    internal const uint PageFaultMarker = uint.MaxValue;
    internal static readonly uint NodeBytes = checked((uint)Unsafe.SizeOf<ClusterBVHNode>());
    private static readonly uint ChildPointerByteOffset = checked(
        (uint)Marshal.OffsetOf<ClusterBVHNode>(nameof(ClusterBVHNode.ChildPointer)).ToInt64());

    private readonly IClusterBvhStorage _storage;
    private readonly List<ClusterBvhAllocation> _allocations = [];
    private readonly List<ClusterBvhPatch> _pendingPatches = [];
    private readonly FlatDictionary<AssetHandle<Mesh>, ClusterRootState> _roots = new();
    private readonly List<ClusterBvhLeafIndex> _leafIndexes = [];
    private readonly List<AssetHandle<Mesh>> _pendingRootMeshes = [];
    private uint _count;
    private uint _registeredPageCount;
    private int _publishedCount;

    internal ClusterBvh(IClusterBvhStorage? storage = null)
        => _storage = storage ?? new SparseClusterBvhStorage(DefaultCapacityBytes);

    public int RegisteredCount => _roots.Count;
    public int PublishedCount => _publishedCount;
    public bool HasPendingPublication
        => _pendingPatches.Count != 0 ||
           _pendingRootMeshes.Count != 0;

    public bool IsRegistered(AssetHandle<Mesh> mesh)
        => _roots.ContainsKey(mesh);

    public bool TryRegisteredRoot(AssetHandle<Mesh> mesh, out uint root)
    {
        if (_roots.TryGetValue(mesh, out ClusterRootState state))
        {
            root = state.NodeIndex;
            return true;
        }
        root = 0;
        return false;
    }

    public bool TryPublishedRoot(AssetHandle<Mesh> mesh, out uint root)
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
        if (TryFindLeafIndexByNode(nodeIndex, out ClusterBvhLeafIndex index))
        {
            uint localPage = index.LocalPageByNode[checked((int)(nodeIndex - index.FirstNode))];
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
        AssetHandle<Mesh> mesh,
        in ClusterBvhDestination destination,
        uint firstPage,
        IReadOnlyList<MeshPayloadPage> pages)
    {
        if (!mesh.IsValid)
            throw new InvalidOperationException("Runtime mesh handle must be valid before Cluster BVH registration.");

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
        ValidateTopology(patched);
        uint[] localPageByNode = PatchNodes(patched, firstNode, pages);

        uint root = firstNode + checked((uint)nodeCount) - 1;
        return new ClusterBvhRegistration(
            mesh,
            firstNode,
            firstPage,
            root,
            destination.Memory.Length,
            nodeCount32,
            pages.Count,
            localPageByNode);
    }

    public uint Commit(in ClusterBvhRegistration registration)
    {
        ulong byteOffset = checked((ulong)registration.FirstNode * NodeBytes);
        _storage.Stage(byteOffset, registration.ByteLength);
        var range = new ClusterBvhAllocation(byteOffset, checked((uint)registration.ByteLength));
        _allocations.Add(range);
        _count += registration.NodeCount;
        _roots.Add(registration.Mesh, new ClusterRootState(registration.Root, Published: false));
        _leafIndexes.Add(new ClusterBvhLeafIndex(
            registration.FirstNode,
            registration.NodeCount,
            registration.FirstPage,
            registration.PageCount,
            registration.LocalPageByNode));
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
            registration.LocalPageByNode is null ||
            registration.LocalPageByNode.Length != checked((int)registration.NodeCount) ||
            _roots.ContainsKey(registration.Mesh))
        {
            throw new InvalidOperationException("The prepared Cluster BVH registration is stale or malformed.");
        }

        _allocations.EnsureCapacity(checked(_allocations.Count + 1));
        _roots.EnsureCapacity(checked(_roots.Count + 1));
        _leafIndexes.EnsureCapacity(checked(_leafIndexes.Count + 1));
        _pendingRootMeshes.EnsureCapacity(checked(_pendingRootMeshes.Count + 1));
    }

    public void ReservePagePatch(uint pageId)
    {
        ClusterBvhLeafIndex index = GetLeafIndexByPage(pageId);
        uint localPage = pageId - index.FirstPage;
        int leafCount = 0;
        foreach (uint candidatePage in index.LocalPageByNode)
        {
            if (candidatePage == localPage)
                leafCount = checked(leafCount + 1);
        }
        _pendingPatches.EnsureCapacity(checked(_pendingPatches.Count + leafCount));
    }

    public void PatchPage(uint pageId, uint pagePointer)
    {
        ClusterBvhLeafIndex index = GetLeafIndexByPage(pageId);
        uint localPage = pageId - index.FirstPage;
        for (int localNode = 0; localNode < index.LocalPageByNode.Length; localNode++)
        {
            if (index.LocalPageByNode[localNode] != localPage)
                continue;
            uint leaf = checked(index.FirstNode + checked((uint)localNode));
            ulong offset = checked((ulong)leaf * NodeBytes + ChildPointerByteOffset);
            _pendingPatches.Add(new ClusterBvhPatch(
                offset,
                pagePointer,
                _storage.GetRange(offset, sizeof(uint))));
        }
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

    public void PreparePublication()
    {
        foreach (ClusterBvhPatch patch in _pendingPatches)
        {
            if (patch.Destination.Length != sizeof(uint))
                throw new InvalidOperationException($"Cluster BVH patch at {patch.Offset} has an invalid destination.");
        }
        foreach (AssetHandle<Mesh> mesh in _pendingRootMeshes)
        {
            if (!_roots.TryGetValue(mesh, out ClusterRootState root) || root.Published)
            {
                throw new InvalidOperationException("Pending Cluster root metadata is inconsistent.");
            }
        }
    }

    public void PublishPending()
    {
        foreach (ClusterBvhPatch patch in _pendingPatches)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                patch.Destination.Span,
                patch.Value);
            _storage.Stage(patch.Offset, sizeof(uint));
        }
        _storage.Publish();
        foreach (AssetHandle<Mesh> mesh in _pendingRootMeshes)
        {
            ClusterRootState root = _roots[mesh];
            _roots[mesh] = root with { Published = true };
            _publishedCount = checked(_publishedCount + 1);
        }

        _pendingPatches.Clear();
        _pendingRootMeshes.Clear();
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

    private static uint[] PatchNodes(
        Span<ClusterBVHNode> nodes,
        uint firstNode,
        IReadOnlyList<MeshPayloadPage> pages)
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("A Cluster BVH must reference at least one page.");

        var pageStates = new PageLeafBuildState[pages.Count];
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeType == 0)
            {
                nodes[i].ChildPointer = checked(nodes[i].ChildPointer + firstNode);
                continue;
            }

            uint localPage = nodes[i].ChildPointer;
            if (localPage >= checked((uint)pages.Count))
            {
                throw new InvalidOperationException(
                    $"BVH leaf node {i} references local page {localPage}, but the mesh contains {pages.Count} pages.");
            }

            nodes[i].GetLeafData(out uint clusterStart, out uint clusterCount);
            int pageIndex = checked((int)localPage);
            uint pageClusterCount = pages[pageIndex].ClusterCount;
            if (clusterCount == 0 || clusterStart > pageClusterCount || clusterCount > pageClusterCount - clusterStart)
            {
                throw new InvalidOperationException(
                    $"BVH leaf node {i} references page {localPage} cluster range " +
                    $"[{clusterStart}, {checked((ulong)clusterStart + clusterCount)}), but the page contains {pageClusterCount} clusters.");
            }

            PageLeafBuildState state = pageStates[pageIndex];
            if (clusterStart != state.CoveredClusters)
            {
                string reason = clusterStart < state.CoveredClusters
                    ? "overlaps a previous ordered range"
                    : "leaves a gap in ordered ranges";
                throw new InvalidOperationException(
                    $"BVH leaf range [{clusterStart}, {checked(clusterStart + clusterCount)}) for page {localPage} {reason} at cluster {state.CoveredClusters}.");
            }
            pageStates[pageIndex] = new PageLeafBuildState(
                checked(state.LeafCount + 1),
                checked(clusterStart + clusterCount));
        }

        for (int page = 0; page < pages.Count; page++)
        {
            PageLeafBuildState state = pageStates[page];
            if (state.LeafCount == 0)
                throw new InvalidOperationException($"Cluster page {page} is not referenced by any BVH leaf.");
            if (state.CoveredClusters != pages[page].ClusterCount)
            {
                throw new InvalidOperationException(
                    $"BVH leaves cover {state.CoveredClusters} clusters on page {page}, but the page contains {pages[page].ClusterCount}.");
            }

        }

        var localPageByNode = new uint[nodes.Length];
        Array.Fill(localPageByNode, uint.MaxValue);
        for (int node = 0; node < nodes.Length; node++)
        {
            if (nodes[node].NodeType != 1)
                continue;

            localPageByNode[node] = nodes[node].ChildPointer;
            nodes[node].ChildPointer = PageFaultMarker;
        }

        return localPageByNode;
    }

    private readonly record struct PageLeafBuildState(int LeafCount, uint CoveredClusters);

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

    private ClusterBvhLeafIndex GetLeafIndexByPage(uint pageId)
    {
        int low = 0;
        int high = _leafIndexes.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ClusterBvhLeafIndex candidate = _leafIndexes[middle];
            if (pageId < candidate.FirstPage)
            {
                high = middle - 1;
                continue;
            }
            if (pageId - candidate.FirstPage >= checked((uint)candidate.PageCount))
            {
                low = middle + 1;
                continue;
            }

            return candidate;
        }

        throw new ArgumentOutOfRangeException(
            nameof(pageId),
            pageId,
            "Cluster page id is outside the BVH leaf index.");
    }

    private static void ValidateTopology(ReadOnlySpan<ClusterBVHNode> nodes)
    {
        if (nodes.IsEmpty)
            throw new InvalidOperationException("A Cluster BVH must contain a root node.");

        int root = nodes.Length - 1;
        int[] parentCounts = new int[nodes.Length];
        for (int node = 0; node < nodes.Length; node++)
        {
            ClusterBVHNode value = nodes[node];
            ValidateNodeData(value, node);
            if (value.NodeType == 1)
                continue;
            if (value.NodeType != 0)
                throw new InvalidOperationException($"BVH node {node} has unsupported node type {value.NodeType}.");

            uint firstChild = value.ChildPointer;
            uint childCount = value.ChildCount;
            if (childCount == 0)
                throw new InvalidOperationException($"BVH internal node {node} has no children.");
            uint length = checked((uint)nodes.Length);
            if (firstChild >= length || childCount > length - firstChild)
                throw new InvalidOperationException($"BVH internal node {node} child range is outside the local node range.");

            for (uint child = 0; child < childCount; child++)
            {
                int childIndex = checked((int)(firstChild + child));
                parentCounts[childIndex] = checked(parentCounts[childIndex] + 1);
                if (parentCounts[childIndex] > 1)
                {
                    throw new InvalidOperationException(
                        $"BVH node {childIndex} has more than one parent.");
                }
            }
        }

        if (parentCounts[root] != 0)
            throw new InvalidOperationException("The final BVH node must be the unique root.");
        for (int node = 0; node < root; node++)
        {
            if (parentCounts[node] != 1)
            {
                throw new InvalidOperationException(
                    $"BVH node {node} must have exactly one parent, but has {parentCounts[node]}.");
            }
        }

        byte[] states = new byte[nodes.Length];
        int[] stackNodes = new int[nodes.Length];
        uint[] stackNextChildren = new uint[nodes.Length];
        int stackCount = 1;
        stackNodes[0] = root;
        states[root] = 1;

        while (stackCount != 0)
        {
            int stackIndex = stackCount - 1;
            int node = stackNodes[stackIndex];
            ClusterBVHNode value = nodes[node];
            if (value.NodeType == 1)
            {
                states[node] = 2;
                stackCount--;
                continue;
            }

            uint nextChild = stackNextChildren[stackIndex];
            if (nextChild < value.ChildCount)
            {
                int child = checked((int)(value.ChildPointer + nextChild));
                stackNextChildren[stackIndex] = nextChild + 1;
                if (states[child] == 1)
                    throw new InvalidOperationException($"BVH node {child} participates in a cycle.");
                if (states[child] == 0)
                {
                    states[child] = 1;
                    stackNodes[stackCount] = child;
                    stackNextChildren[stackCount] = 0;
                    stackCount++;
                }
                continue;
            }

            states[node] = 2;
            stackCount--;
        }

        for (int node = 0; node < states.Length; node++)
        {
            if (states[node] != 2)
                throw new InvalidOperationException($"BVH node {node} is not reachable from the final root.");
        }

        ValidateTraversalStack(nodes, root, stackNodes);
    }

    private static void ValidateTraversalStack(
        ReadOnlySpan<ClusterBVHNode> nodes,
        int root,
        Span<int> stack)
    {
        int stackCount = 1;
        stack[0] = root;
        while (stackCount != 0)
        {
            int node = stack[--stackCount];
            ClusterBVHNode value = nodes[node];
            if (value.NodeType == 1)
                continue;

            int childCount = checked((int)value.ChildCount);
            if (childCount > TraversalStackCapacity - stackCount)
            {
                throw new InvalidOperationException(
                    $"BVH node {node} requires {checked(stackCount + childCount)} pending traversal " +
                    $"entries, but the Cluster shader supports {TraversalStackCapacity}.");
            }

            for (int child = 0; child < childCount; child++)
                stack[stackCount++] = checked((int)value.ChildPointer + child);
        }
    }

    private static void ValidateNodeData(in ClusterBVHNode node, int nodeIndex)
    {
        if (!IsFinite(node.BoundMin) ||
            !IsFinite(node.BoundMax) ||
            node.BoundMin.X > node.BoundMax.X ||
            node.BoundMin.Y > node.BoundMax.Y ||
            node.BoundMin.Z > node.BoundMax.Z ||
            !IsFinite(node.LODSphere) ||
            node.LODSphere.W < 0 ||
            !float.IsFinite(node.LODError) ||
            node.LODError < 0)
        {
            throw new InvalidOperationException(
                $"BVH node {nodeIndex} contains invalid bounds or LOD data.");
        }
    }

    private static bool IsFinite(in System.Numerics.Vector4 value)
        => float.IsFinite(value.X) &&
           float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) &&
           float.IsFinite(value.W);

}


