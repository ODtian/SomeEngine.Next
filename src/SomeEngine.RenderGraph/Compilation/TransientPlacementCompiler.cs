namespace SomeEngine.RenderGraph;

using System.Runtime.CompilerServices;

internal static class TransientPlacementCompiler
{
    public static unsafe ArenaSlice<PlannedAliasingBarrier> Place(
        RenderGraph graph,
        ReachabilityTable? order,
        bool enableAliasing,
        ArenaSlice<int> accessPassOrdinals = default,
        ArenaSlice<int> resourceAccessOffsets = default,
        ArenaSlice<int> resourceAccessOrdinals = default)
    {
        int resourceCount = graph.ResourceCount;
        int passCount = graph.Passes.Length;
        int bufferCount = graph.Buffers.Length;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        ResourceUnversionedData* canonicalBufferRows =
            graph.Buffers.DangerousContiguousPointer;
        ResourceUnversionedData* canonicalTextureRows =
            graph.Textures.DangerousContiguousPointer;
        GraphMemoryRequirements* requirementRows =
            graph.ResourceRequirementRows.DangerousPointer;
        int transientCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceLiveFlag) == 0)
            {
                continue;
            }
            bool imported = resource < bufferCount
                ? (canonicalBufferRows is not null
                    ? canonicalBufferRows[resource].IsImported
                    : graph.GetBufferByResourceOrdinal(resource).IsImported)
                : (canonicalTextureRows is not null
                    ? canonicalTextureRows[resource - bufferCount].IsImported
                    : graph.GetTextureByResourceOrdinal(resource).IsImported);
            if (!imported) transientCount++;
        }

        ArenaSlice<int> placementHeaps =
            graph.AllocateSlice<int>(resourceCount, clear: false);
        ArenaSlice<ulong> placementOffsets =
            graph.AllocateSlice<ulong>(resourceCount);
        placementHeaps.Span.Fill(-1);
        int* placementHeapRows = placementHeaps.DangerousPointer;
        ulong* placementOffsetRows = placementOffsets.DangerousPointer;
        if (transientCount == 0)
        {
            graph.Heaps = default;
            graph.PlacementHeaps = placementHeaps;
            graph.PlacementOffsets = placementOffsets;
            graph.Aliasing = new AliasingStatistics(enableAliasing, 0, 0, 0, 0, 0, 0);
            return default;
        }

        if (enableAliasing && order is null)
            throw new ArgumentNullException(nameof(order), "Transient aliasing requires pass reachability.");
        ReachabilityTable reachability = order.GetValueOrDefault();
        ResourceOccurrenceIndex uses = BuildResourceUses(
            graph,
            graph.ActivePassOrdinals,
            storeUseRows: enableAliasing && !reachability.IsTotalOrder,
            reachability,
            accessPassOrdinals,
            resourceAccessOffsets,
            resourceAccessOrdinals);
        ArenaSlice<int> resources =
            graph.AllocateSlice<int>(transientCount, clear: false);
        int* placementResources =
            resources.DangerousPointer;
        ulong logicalRequestedBytes = 0;
        for (int resource = 0, destination = 0;
             resource < resourceCount;
             resource++)
        {
            if ((liveFlags[passCount + resource] &
                 RenderGraph.ResourceLiveFlag) == 0)
            {
                continue;
            }
            bool imported = resource < bufferCount
                ? (canonicalBufferRows is not null
                    ? canonicalBufferRows[resource].IsImported
                    : graph.GetBufferByResourceOrdinal(resource).IsImported)
                : (canonicalTextureRows is not null
                    ? canonicalTextureRows[resource - bufferCount].IsImported
                    : graph.GetTextureByResourceOrdinal(resource).IsImported);
            if (imported) continue;
            if (uses.Count(resource) == 0)
                throw new InvalidOperationException("A live transient resource has no live pass use.");
            GraphMemoryRequirements requirements = requirementRows[resource];
            placementResources[destination++] = resource;
            logicalRequestedBytes = checked(logicalRequestedBytes + requirements.Size);
        }
        SortPlacementResources(
            placementResources,
            resources.Length,
            requirementRows,
            in uses);

        int heapCount = 0;
        ulong nonAliasedPlacedBytes = 0;
        for (int index = 0; index < resources.Length;)
        {
            int resource = placementResources[index];
            ProfileKey profile = ProfileKey.From(requirementRows[resource]);
            ulong size = 0;
            do
            {
                resource = placementResources[index++];
                GraphMemoryRequirements requirements = requirementRows[resource];
                if (ProfileKey.From(requirements) != profile)
                {
                    index--;
                    break;
                }
                size = checked(
                    AlignUp(size, requirements.Alignment) +
                    AlignUp(requirements.Size, requirements.Alignment));
            }
            while (index < resources.Length);
            nonAliasedPlacedBytes = checked(nonAliasedPlacedBytes + size);
            heapCount++;
        }

        ArenaSlice<GraphMemoryRequirements> heaps =
            graph.AllocateSlice<GraphMemoryRequirements>(heapCount, clear: false);
        ArenaSlice<PlacementCandidate> intervals =
            graph.AllocateSlice<PlacementCandidate>(transientCount, clear: false);
        ArenaSlice<PlannedAliasingBarrier> aliasEdges =
            graph.AllocateSlice<PlannedAliasingBarrier>(transientCount, clear: false);
        GraphMemoryRequirements* heapRows = heaps.DangerousPointer;
        PlacementCandidate* intervalRows =
            intervals.DangerousPointer;
        PlannedAliasingBarrier* aliasRows =
            aliasEdges.DangerousPointer;
        int intervalCount = 0;
        int aliasCount = 0;
        int heapOrdinal = 0;
        int scan = 0;
        while (scan < resources.Length)
        {
            int resource = placementResources[scan];
            ProfileKey profile = ProfileKey.From(requirementRows[resource]);
            int intervalStart = intervalCount;
            ulong heapSize = 0;
            ulong heapAlignment = 0;
            do
            {
                resource = placementResources[scan];
                GraphMemoryRequirements requirements = requirementRows[resource];
                if (ProfileKey.From(requirements) != profile) break;
                heapAlignment = Math.Max(heapAlignment, requirements.Alignment);
                ulong footprint =
                    AlignUp(requirements.Size, requirements.Alignment);
                bool aliasable = enableAliasing &&
                    requirements.MemoryType == MemoryType.DeviceLocal &&
                    FirstUsesInitialize(graph, resource, uses, reachability);
                int selected = -1;
                if (aliasable)
                {
                    for (int intervalOrdinal = intervalStart; intervalOrdinal < intervalCount; intervalOrdinal++)
                    {
                        PlacementCandidate interval =
                            intervalRows[intervalOrdinal];
                        if (!interval.Aliasable ||
                            interval.Capacity < requirements.Size ||
                            interval.Offset %
                                requirements.Alignment != 0)
                        {
                            continue;
                        }
                        bool lifetimeBefore =
                            interval.LastResource >= 0 &&
                            (reachability.IsTotalOrder
                                ? reachability.Before(
                                    uses.Last(interval.LastResource),
                                    uses.First(resource))
                                : uses.LifetimeBefore(
                                    interval.LastResource,
                                    resource));
                        if (!lifetimeBefore)
                        {
                            continue;
                        }
                        selected = intervalOrdinal;
                        break;
                    }
                }

                if (selected < 0)
                {
                    ulong offset = AlignUp(heapSize, requirements.Alignment);
                    ulong capacity = footprint;
                    selected = intervalCount++;
                    intervalRows[selected] = new PlacementCandidate(
                        offset,
                        capacity,
                        aliasable,
                        -1);
                    heapSize = checked(offset + capacity);
                }
                else
                {
                    int previous =
                        intervalRows[selected].LastResource;
                    aliasRows[aliasCount++] = new PlannedAliasingBarrier(
                        previous,
                        resource,
                        uses.EndFrontier(graph, previous, reachability),
                        uses.StartFrontier(graph, resource, reachability));
                }

                intervalRows[selected] = intervalRows[selected] with
                {
                    LastResource = resource,
                };
                placementHeapRows[resource] = heapOrdinal;
                placementOffsetRows[resource] =
                    intervalRows[selected].Offset;
                scan++;
            }
            while (scan < resources.Length);

            heapRows[heapOrdinal++] = new GraphMemoryRequirements(
                heapSize,
                heapAlignment,
                profile.MemoryType,
                profile.Flags);
        }

        ulong plannedHeapBytes = 0;
        for (int heap = 0; heap < heapCount; heap++)
            plannedHeapBytes =
                checked(plannedHeapBytes + heapRows[heap].Size);
        graph.Heaps = heaps;
        graph.PlacementHeaps = placementHeaps;
        graph.PlacementOffsets = placementOffsets;
        graph.Aliasing = new AliasingStatistics(
            enableAliasing,
            logicalRequestedBytes,
            nonAliasedPlacedBytes,
            plannedHeapBytes,
            nonAliasedPlacedBytes >= plannedHeapBytes
                ? nonAliasedPlacedBytes - plannedHeapBytes
                : 0,
            intervalCount,
            aliasCount);
        return aliasCount == 0 ? default : aliasEdges.Slice(0, aliasCount);
    }

    private static unsafe void SortPlacementResources(
        int* resources,
        int count,
        GraphMemoryRequirements* requirements,
        in ResourceOccurrenceIndex uses)
    {
        int gap = 1;
        while (gap < count / 3)
            gap = checked(gap * 3 + 1);
        while (gap >= 1)
        {
            for (int index = gap; index < count; index++)
            {
                int value = resources[index];
                int destination = index;
                while (destination >= gap &&
                       ComparePlacementResources(
                           value,
                           resources[destination - gap],
                           requirements,
                           in uses) < 0)
                {
                    resources[destination] = resources[destination - gap];
                    destination -= gap;
                }
                resources[destination] = value;
            }
            gap /= 3;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ComparePlacementResources(
        int left,
        int right,
        GraphMemoryRequirements* requirementRows,
        in ResourceOccurrenceIndex uses)
    {
        GraphMemoryRequirements leftRequirements = requirementRows[left];
        GraphMemoryRequirements rightRequirements = requirementRows[right];
        ProfileKey leftProfile = ProfileKey.From(leftRequirements);
        ProfileKey rightProfile = ProfileKey.From(rightRequirements);
        int leftMemory = (int)leftProfile.MemoryType;
        int rightMemory = (int)rightProfile.MemoryType;
        if (leftMemory != rightMemory)
            return leftMemory < rightMemory ? -1 : 1;

        int leftFlags = (int)leftProfile.Flags;
        int rightFlags = (int)rightProfile.Flags;
        if (leftFlags != rightFlags)
            return leftFlags < rightFlags ? -1 : 1;

        int leftFirstUse = uses.First(left);
        int rightFirstUse = uses.First(right);
        if (leftFirstUse != rightFirstUse)
            return leftFirstUse < rightFirstUse ? -1 : 1;

        ulong leftSize = leftRequirements.Size;
        ulong rightSize = rightRequirements.Size;
        if (leftSize != rightSize)
            return leftSize > rightSize ? -1 : 1;

        if (left == right)
            return 0;
        return left < right ? -1 : 1;
    }

    private static ResourceOccurrenceIndex BuildResourceUses(
        RenderGraph graph,
        ArenaSlice<int> activePassOrdinals,
        bool storeUseRows,
        ReachabilityTable reachability,
        ArenaSlice<int> accessPassOrdinals,
        ArenaSlice<int> resourceAccessOffsets,
        ArenaSlice<int> resourceAccessOrdinals)
    {
        if (resourceAccessOffsets.Length == graph.ResourceCount + 1 &&
            resourceAccessOrdinals.Length == graph.PassInputs.Length &&
            accessPassOrdinals.Length == graph.PassInputs.Length)
        {
            return BuildIndexedResourceUses(
                graph,
                storeUseRows,
                reachability,
                accessPassOrdinals,
                resourceAccessOffsets,
                resourceAccessOrdinals);
        }

        return BuildPassIndexedResourceUses(
            graph,
            activePassOrdinals,
            storeUseRows,
            reachability);
    }

    private static unsafe ResourceOccurrenceIndex BuildIndexedResourceUses(
        RenderGraph graph,
        bool storeUseRows,
        ReachabilityTable reachability,
        ArenaSlice<int> accessPassOrdinals,
        ArenaSlice<int> resourceAccessOffsets,
        ArenaSlice<int> resourceAccessOrdinals)
    {
        int resourceCount = graph.ResourceCount;
        int passCount = graph.Passes.Length;
        ArenaSlice<int> rows = storeUseRows
            ? graph.AllocateSlice<int>(
                resourceAccessOrdinals.Length,
                clear: false)
            : default;
        ResourceOccurrenceIndex resources =
            ResourceOccurrenceIndex.Create(graph, resourceCount, rows);
        int* occurrenceRows = rows.DangerousPointer;
        int* accessPassRows =
            accessPassOrdinals.DangerousPointer;
        int* resourceOffsetRows =
            resourceAccessOffsets.DangerousPointer;
        int* resourcePassInputs =
            resourceAccessOrdinals.DangerousPointer;
        byte* liveFlags = graph.LivenessFlags.DangerousPointer;
        int useCount = 0;
        for (int resource = 0; resource < resourceCount; resource++)
        {
            resources.SetOffset(resource, useCount);
            int previousPass = -1;
            for (int index = resourceOffsetRows[resource];
                 index < resourceOffsetRows[resource + 1];
                 index++)
            {
                int pass = accessPassRows[
                    resourcePassInputs[index]];
                if ((liveFlags[pass] &
                     RenderGraph.PassLiveFlag) == 0 ||
                    pass == previousPass)
                {
                    continue;
                }
                if (resources.Count(resource) == 0)
                    resources.SetFirst(resource, pass);
                resources.SetLast(resource, pass);
                resources.IncrementCount(resource);
                previousPass = pass;
                if (storeUseRows) occurrenceRows[useCount] = pass;
                useCount++;
            }
        }
        if (!storeUseRows)
            return resources;

        return BuildResourceFrontiers(
            graph,
            resources,
            useCount,
            reachability);
    }

    private static ResourceOccurrenceIndex BuildPassIndexedResourceUses(
        RenderGraph graph,
        ArenaSlice<int> activePassOrdinals,
        bool storeUseRows,
        ReachabilityTable reachability)
    {
        ResourceOccurrenceIndex resources =
            ResourceOccurrenceIndex.Create(graph, graph.ResourceCount);
        ArenaSlice<int> marks = graph.AllocateSlice<int>(graph.ResourceCount);
        int stamp = 0;
        foreach (int pass in activePassOrdinals)
        {
            stamp++;
            ReadOnlySpan<PassInputData> accesses = graph.GetPassAccesses(graph.Passes[pass]);
            for (int accessOrdinal = 0; accessOrdinal < accesses.Length; accessOrdinal++)
            {
                int resource = graph.GetResourceOrdinal(accesses[accessOrdinal]);
                if (marks[resource] == stamp) continue;
                marks[resource] = stamp;
                if (resources.Count(resource) == 0)
                    resources.SetFirst(resource, pass);
                resources.SetLast(resource, pass);
                resources.IncrementCount(resource);
            }
        }
        if (!storeUseRows)
            return resources;

        int useCount = 0;
        for (int resource = 0; resource < graph.ResourceCount; resource++)
        {
            resources.SetOffset(resource, useCount);
            useCount = checked(useCount + resources.Count(resource));
        }
        ArenaSlice<int> rows = graph.AllocateSlice<int>(useCount, clear: false);
        resources = resources.WithRows(rows);
        ArenaSlice<int> cursors = graph.AllocateSlice<int>(graph.ResourceCount, clear: false);
        for (int resource = 0; resource < graph.ResourceCount; resource++)
            cursors[resource] = resources.Offset(resource);
        marks.Span.Clear();
        stamp = 0;
        foreach (int pass in activePassOrdinals)
        {
            stamp++;
            ReadOnlySpan<PassInputData> accesses = graph.GetPassAccesses(graph.Passes[pass]);
            for (int accessOrdinal = 0; accessOrdinal < accesses.Length; accessOrdinal++)
            {
                int resource = graph.GetResourceOrdinal(accesses[accessOrdinal]);
                if (marks[resource] == stamp) continue;
                marks[resource] = stamp;
                rows[cursors[resource]++] = pass;
            }
        }
        return BuildResourceFrontiers(
            graph,
            resources,
            useCount,
            reachability);
    }

    private static ResourceOccurrenceIndex BuildResourceFrontiers(
        RenderGraph graph,
        ResourceOccurrenceIndex resources,
        int useCount,
        ReachabilityTable reachability)
    {
        ArenaSlice<int> frontiers =
            graph.AllocateSlice<int>(checked(useCount * 2), clear: false);
        int lifetimeWordCount = reachability.WordCount;
        ArenaSlice<ulong> lifetimeMasks = graph.AllocateSlice<ulong>(
            checked(graph.ResourceCount * lifetimeWordCount * 2));
        Span<int> frontierRows = frontiers.Span;
        Span<ulong> lifetimeRows = lifetimeMasks.Span;
        int frontierCursor = 0;
        for (int resource = 0; resource < graph.ResourceCount; resource++)
        {
            int count = resources.Count(resource);
            if (count == 0) continue;
            ReadOnlySpan<int> resourceUses = resources.Get(resource);

            resources.SetStartOffset(resource, frontierCursor);
            int startCount = reachability.WriteOrderedFrontier(
                resourceUses,
                frontierRows.Slice(frontierCursor, resourceUses.Length),
                start: true);
            resources.SetStartCount(resource, startCount);
            frontierCursor = checked(frontierCursor + startCount);

            resources.SetEndOffset(resource, frontierCursor);
            int endCount = reachability.WriteOrderedFrontier(
                resourceUses,
                frontierRows.Slice(frontierCursor, resourceUses.Length),
                start: false);
            resources.SetEndCount(resource, endCount);
            frontierCursor = checked(frontierCursor + endCount);

            int lifetimeOffset =
                checked(resource * lifetimeWordCount * 2);
            reachability.WritePositionMask(
                frontierRows.Slice(resources.EndOffset(resource), endCount),
                lifetimeRows.Slice(
                    lifetimeOffset,
                    lifetimeWordCount));
            reachability.WriteCommonAncestorMask(
                frontierRows.Slice(resources.StartOffset(resource), startCount),
                lifetimeRows.Slice(
                    lifetimeOffset + lifetimeWordCount,
                    lifetimeWordCount));
        }
        return resources.WithFrontiers(
            frontiers.Slice(0, frontierCursor),
            lifetimeMasks,
            lifetimeWordCount);
    }

    private static unsafe bool FirstUsesInitialize(
        RenderGraph graph,
        int resource,
        in ResourceOccurrenceIndex uses,
        ReachabilityTable order)
    {
        if (uses.Count(resource) == 0) return false;
        int bufferCount = graph.Buffers.Length;
        PassData* canonicalPassRows =
            graph.Passes.DangerousContiguousPointer;
        PassInputData* canonicalPassInputs =
            graph.PassInputs.DangerousContiguousPointer;
        if (order.IsTotalOrder)
            return FirstPassInitializes(
                graph,
                resource,
                uses.First(resource),
                bufferCount,
                canonicalPassRows,
                canonicalPassInputs);
        bool foundStart = false;
        foreach (int candidate in uses.GetStart(resource))
        {
            foundStart = true;
            if (!FirstPassInitializes(
                    graph,
                    resource,
                    candidate,
                    bufferCount,
                    canonicalPassRows,
                    canonicalPassInputs))
            {
                return false;
            }
        }
        return foundStart;
    }

    private static unsafe bool FirstPassInitializes(
        RenderGraph graph,
        int resource,
        int pass,
        int bufferCount,
        PassData* canonicalPassRows,
        PassInputData* canonicalPassInputs)
    {
        PassData passRow = canonicalPassRows is not null
            ? canonicalPassRows[pass]
            : graph.Passes[pass];
        ReadOnlySpan<PassInputData> accesses =
            canonicalPassInputs is not null
                ? new ReadOnlySpan<PassInputData>(
                    canonicalPassInputs + passRow.AccessOffset,
                    passRow.AccessCount)
                : graph.GetPassAccesses(passRow);
        bool foundAccess = false;
        foreach (ref readonly PassInputData access in accesses)
        {
            int accessedResource = access.IsBuffer
                ? access.Buffer
                : checked(bufferCount + access.Texture);
            if (accessedResource != resource) continue;
            foundAccess = true;
            if ((access.Flags & GraphAccess.WriteAll) != GraphAccess.WriteAll)
            {
                return false;
            }
        }
        if (!foundAccess)
            throw new InvalidOperationException("A resource start frontier has no matching access.");
        return true;
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        alignment <= 1 ? value : checked(((value + alignment - 1) / alignment) * alignment);

    private readonly unsafe struct ResourceOccurrenceIndex
    {
        private const int FieldCount = 8;
        private readonly ArenaSlice<int> _workspace;
        private readonly ArenaSlice<int> _rowStorage;
        private readonly ArenaSlice<int> _frontierStorage;
        private readonly int _resourceCount;
        private readonly int* _offsets;
        private readonly int* _counts;
        private readonly int* _first;
        private readonly int* _last;
        private readonly int* _startOffsets;
        private readonly int* _startCounts;
        private readonly int* _endOffsets;
        private readonly int* _endCounts;
        private readonly int* _rows;
        private readonly int* _frontiers;
        private readonly ulong* _lifetimeMasks;
        private readonly int _lifetimeWordCount;

        private ResourceOccurrenceIndex(
            ArenaSlice<int> workspace,
            int resourceCount,
            ArenaSlice<int> rows,
            ArenaSlice<int> frontiers,
            ArenaSlice<ulong> lifetimeMasks = default,
            int lifetimeWordCount = 0)
        {
            _workspace = workspace;
            _rowStorage = rows;
            _frontierStorage = frontiers;
            _resourceCount = resourceCount;
            int* fields = workspace.DangerousPointer;
            _offsets = fields;
            _counts = fields + resourceCount;
            _first = fields + resourceCount * 2;
            _last = fields + resourceCount * 3;
            _startOffsets = fields + resourceCount * 4;
            _startCounts = fields + resourceCount * 5;
            _endOffsets = fields + resourceCount * 6;
            _endCounts = fields + resourceCount * 7;
            _rows = rows.DangerousPointer;
            _frontiers = frontiers.DangerousPointer;
            _lifetimeMasks = lifetimeMasks.DangerousPointer;
            _lifetimeWordCount = lifetimeWordCount;
        }

        internal static ResourceOccurrenceIndex Create(
            RenderGraph graph,
            int resourceCount,
            ArenaSlice<int> rows = default) =>
            new(
                graph.AllocateSlice<int>(checked(resourceCount * FieldCount)),
                resourceCount,
                rows,
                default);

        internal ResourceOccurrenceIndex WithRows(ArenaSlice<int> rows) =>
            new(
                _workspace,
                _resourceCount,
                rows,
                _frontierStorage,
                lifetimeMasks: default,
                lifetimeWordCount: 0);

        internal ResourceOccurrenceIndex WithFrontiers(
            ArenaSlice<int> frontiers,
            ArenaSlice<ulong> lifetimeMasks,
            int lifetimeWordCount) =>
            new(
                _workspace,
                _resourceCount,
                _rowStorage,
                frontiers,
                lifetimeMasks,
                lifetimeWordCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Offset(int resource) => _offsets[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Count(int resource) => _counts[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int First(int resource) => _first[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Last(int resource) => _last[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int StartOffset(int resource) => _startOffsets[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int EndOffset(int resource) => _endOffsets[resource];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetOffset(int resource, int value) => _offsets[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetFirst(int resource, int value) => _first[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetLast(int resource, int value) => _last[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementCount(int resource) => _counts[resource]++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetStartOffset(int resource, int value) => _startOffsets[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetStartCount(int resource, int value) => _startCounts[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetEndOffset(int resource, int value) => _endOffsets[resource] = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetEndCount(int resource, int value) => _endCounts[resource] = value;

        internal ReadOnlySpan<int> Get(int resource) =>
            new(_rows + _offsets[resource], _counts[resource]);

        internal ReadOnlySpan<int> GetStart(int resource) =>
            new(_frontiers + _startOffsets[resource], _startCounts[resource]);

        internal ReadOnlySpan<int> GetEnd(int resource) =>
            new(_frontiers + _endOffsets[resource], _endCounts[resource]);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool LifetimeBefore(
            int beforeResource,
            int afterResource)
        {
            if (_lifetimeMasks is null ||
                _lifetimeWordCount <= 0)
            {
                throw new InvalidOperationException(
                    "Partial-order lifetime masks were not materialized.");
            }
            ulong* beforeEnds = _lifetimeMasks +
                checked(beforeResource * _lifetimeWordCount * 2);
            ulong* afterAncestors = _lifetimeMasks +
                checked(
                    afterResource * _lifetimeWordCount * 2 +
                    _lifetimeWordCount);
            if (_lifetimeWordCount == 1)
                return (beforeEnds[0] & ~afterAncestors[0]) == 0;
            for (int word = 0;
                 word < _lifetimeWordCount;
                 word++)
            {
                if ((beforeEnds[word] & ~afterAncestors[word]) != 0)
                    return false;
            }
            return true;
        }

        internal ArenaSlice<int> StartFrontier(
            RenderGraph graph,
            int resource,
            ReachabilityTable order)
        {
            if (!order.IsTotalOrder)
            {
                return _frontierStorage.Slice(
                    _startOffsets[resource],
                    _startCounts[resource]);
            }
            ArenaSlice<int> result = graph.AllocateSlice<int>(1, clear: false);
            result[0] = First(resource);
            return result;
        }

        internal ArenaSlice<int> EndFrontier(
            RenderGraph graph,
            int resource,
            ReachabilityTable order)
        {
            if (!order.IsTotalOrder)
            {
                return _frontierStorage.Slice(
                    _endOffsets[resource],
                    _endCounts[resource]);
            }
            ArenaSlice<int> result = graph.AllocateSlice<int>(1, clear: false);
            result[0] = Last(resource);
            return result;
        }
    }

    private readonly record struct PlacementCandidate(
        ulong Offset,
        ulong Capacity,
        bool Aliasable,
        int LastResource);

    private readonly record struct ProfileKey(
        MemoryType MemoryType,
        HeapFlags Flags)
    {
        internal static ProfileKey From(in GraphMemoryRequirements requirements) =>
            new(
                requirements.MemoryType,
                requirements.Flags);
    }
}
