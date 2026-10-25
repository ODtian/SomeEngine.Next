namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private static readonly global::SomeEngine.Diagnostics.TraceSite ResourceValuesTrace = new("rg", "ResourceValues");
    private static readonly global::SomeEngine.Diagnostics.TraceSite ResourceHazardsTrace = new("rg", "ResourceHazards");
    private static readonly global::SomeEngine.Diagnostics.TraceSite LivenessTrace = new("rg", "Liveness");
    private static readonly global::SomeEngine.Diagnostics.TraceSite ResourceOrderTrace = new("rg", "ResourceOrder");
    private void ResolveBufferValues(ResourceAnalysis analysis, int bufferIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;

        FrameBuffer buffer = _buffers[bufferIndex];
        if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent && buffer.Definition is not null)
            buffer.EntryBoundaryStates = buffer.Definition.BoundaryStates;
        else if (buffer.Ownership == RenderGraphResourceOwnership.GraphOwned)
            buffer.EntryBoundaryStates = null;
        int coordinateCount = BuildBufferCoordinates(analysis, buffer, accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref analysis.Writers, segmentCount);
        EnsureCapacity(ref analysis.Contents, segmentCount);
        EnsureCapacity(ref analysis.ContentScopes, segmentCount);
        Array.Fill(analysis.Writers, -1, 0, segmentCount);
        Array.Clear(analysis.Contents, 0, segmentCount);
        Array.Clear(analysis.ContentScopes, 0, segmentCount);
        PrepareSharedWriterScratch(analysis, segmentCount);
        InitializeBufferContents(
            buffer,
            analysis.BufferCoordinates.AsSpan(0, coordinateCount),
            analysis.Contents.AsSpan(0, segmentCount),
            analysis.ContentScopes.AsSpan(0, segmentCount));

        SortAccessesByValueOrder(analysis, accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (BufferRange range in access.BufferRanges)
            {
                int first = BinarySearch(
                    analysis.BufferCoordinates.AsSpan(0, coordinateCount),
                    range.Offset);
                int last = BinarySearch(
                    analysis.BufferCoordinates.AsSpan(0, coordinateCount),
                    checked(range.Offset + range.Size));
                if (first < 0 || last < 0)
                    throw new InvalidOperationException("RG9001: Buffer coordinate compression is incomplete.");
                for (int segment = first; segment < last; segment++)
                {
                    bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                        access.Mode == GraphAccessMode.Write && access.Coverage == WriteCoverage.Partial;
                    if (access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite &&
                        !HasRequiredBufferContents(
                            analysis.Contents[segment],
                            analysis.ContentScopes[segment],
                            access.BufferContentScope))
                    {
                        throw new UndefinedResourceReadException(
                            $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Buffer " +
                            $"'{buffer.Description.Label ?? $"Buffer[{bufferIndex}]"}'. " +
                            string.Join("; ", accesses.Select(i => $"{_passes[_accesses[i].PassIndex].Label}: {_accesses[i].Mode}/{_accesses[i].Coverage} at declaration {_passes[_accesses[i].PassIndex].DeclarationOrdinal}")));
                    }
                    if (reads && analysis.Writers[segment] >= 0)
                        AddValueDependency(analysis, access.PassIndex, analysis.Writers[segment]);
                    if (reads && access.UnorderedAccessHazards == UnorderedAccessHazardMode.Ordered)
                        foreach (int writer in analysis.SharedWriters[segment])
                            AddValueDependency(analysis, access.PassIndex, writer);

                    if (access.Mode != GraphAccessMode.Read)
                    {
                        if (access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered)
                        {
                            AddUnique(analysis.SharedWriters[segment], access.PassIndex);
                            if (access.Mode == GraphAccessMode.ReadWrite || access.ResultContents.HasValue)
                            {
                                analysis.Contents[segment] = ResourceContentState.Defined;
                                analysis.ContentScopes[segment] = access.BufferContentScope;
                            }
                            continue;
                        }
                        analysis.SharedWriters[segment].Clear();
                        analysis.Writers[segment] = access.PassIndex;
                        if (access.ResultContents.HasValue)
                        {
                            analysis.Contents[segment] = access.ResultContents.Value;
                            analysis.ContentScopes[segment] =
                                access.ResultContents.Value == ResourceContentState.Defined
                                    ? access.BufferContentScope
                                    : BufferContentScope.FullRange;
                        }
                        else if (access.Mode == GraphAccessMode.ReadWrite)
                        {
                            analysis.Contents[segment] = ResourceContentState.Defined;
                            analysis.ContentScopes[segment] = access.BufferContentScope;
                        }
                        else if (access.Coverage == WriteCoverage.Complete)
                        {
                            analysis.Contents[segment] = ResourceContentState.Defined;
                            analysis.ContentScopes[segment] = BufferContentScope.FullRange;
                        }
                    }
                }
            }
        }

        if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent ||
            buffer.Ownership == RenderGraphResourceOwnership.CallerOwned)
        {
            for (int segment = 0; segment < segmentCount; segment++)
            {
                if (analysis.Writers[segment] >= 0) AddLiveRoot(analysis, analysis.Writers[segment]);
                foreach (int writer in analysis.SharedWriters[segment]) AddLiveRoot(analysis, writer);
            }
        }
    }

    private void ResolveTextureValues(ResourceAnalysis analysis, int textureIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;

        FrameTexture texture = _textures[textureIndex];
        if (texture.Lifetime == RenderGraphResourceLifetime.Persistent && texture.Definition is not null)
            texture.EntryBoundaryStates = texture.Definition.BoundaryStates;
        else if (texture.Ownership == RenderGraphResourceOwnership.GraphOwned)
            texture.EntryBoundaryStates = null;
        int cellCount = BuildTextureCoordinates(analysis, texture, accesses);
        EnsureCapacity(ref analysis.Writers, cellCount);
        EnsureCapacity(ref analysis.Contents, cellCount);
        Array.Fill(analysis.Writers, -1, 0, cellCount);
        Array.Clear(analysis.Contents, 0, cellCount);
        PrepareSharedWriterScratch(analysis, cellCount);
        InitializeTextureContents(analysis, texture, analysis.Contents.AsSpan(0, cellCount));

        SortAccessesByValueOrder(analysis, accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (int cell in TextureRegions(analysis, texture, access.TextureRange))
            {
                bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                    access.Mode == GraphAccessMode.Write && access.Coverage == WriteCoverage.Partial;
                if (access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite &&
                    analysis.Contents[cell] != ResourceContentState.Defined)
                {
                    throw new UndefinedResourceReadException(
                        $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Texture " +
                        $"'{texture.Label ?? $"Texture[{textureIndex}]"}'. " +
                        string.Join("; ", accesses.Select(i => $"{_passes[_accesses[i].PassIndex].Label}: {_accesses[i].Mode}/{_accesses[i].Coverage} at declaration {_passes[_accesses[i].PassIndex].DeclarationOrdinal}")));
                }
                if (reads && analysis.Writers[cell] >= 0)
                    AddValueDependency(analysis, access.PassIndex, analysis.Writers[cell]);
                if (reads && access.UnorderedAccessHazards == UnorderedAccessHazardMode.Ordered)
                    foreach (int writer in analysis.SharedWriters[cell])
                        AddValueDependency(analysis, access.PassIndex, writer);
                if (access.Mode != GraphAccessMode.Read)
                {
                    if (access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered)
                    {
                        AddUnique(analysis.SharedWriters[cell], access.PassIndex);
                        if (access.Mode == GraphAccessMode.ReadWrite || access.ResultContents.HasValue)
                            analysis.Contents[cell] = ResourceContentState.Defined;
                        continue;
                    }
                    analysis.SharedWriters[cell].Clear();
                    analysis.Writers[cell] = access.PassIndex;
                    if (access.ResultContents.HasValue)
                    {
                        analysis.Contents[cell] = access.ResultContents.Value;
                    }
                    else if (access.Mode == GraphAccessMode.ReadWrite ||
                        access.Coverage == WriteCoverage.Complete)
                    {
                        analysis.Contents[cell] = ResourceContentState.Defined;
                    }
                }
            }
        }

        if (texture.Lifetime == RenderGraphResourceLifetime.Persistent ||
            texture.Ownership == RenderGraphResourceOwnership.CallerOwned)
        {
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (analysis.Writers[cell] >= 0) AddLiveRoot(analysis, analysis.Writers[cell]);
                foreach (int writer in analysis.SharedWriters[cell]) AddLiveRoot(analysis, writer);
            }
        }
    }

    private void ResolveQueryValues(ResourceAnalysis analysis, int poolIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;

        FrameQueryPool pool = _queryPools[poolIndex];
        int coordinateCount = BuildQueryCoordinates(analysis, pool, accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref analysis.Writers, segmentCount);
        EnsureCapacity(ref analysis.Contents, segmentCount);
        Array.Fill(analysis.Writers, -1, 0, segmentCount);
        Array.Clear(analysis.Contents, 0, segmentCount);
        InitializeQueryContents(
            pool,
            analysis.QueryCoordinates.AsSpan(0, coordinateCount),
            analysis.Contents.AsSpan(0, segmentCount));

        SortAccessesByValueOrder(analysis, accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            int first = BinarySearch(
                analysis.QueryCoordinates.AsSpan(0, coordinateCount),
                access.QueryRange.FirstQuery);
            int last = BinarySearch(
                analysis.QueryCoordinates.AsSpan(0, coordinateCount),
                checked(access.QueryRange.FirstQuery + access.QueryRange.QueryCount));
            if (first < 0 || last < 0)
                throw new InvalidOperationException("RG9001: Query coordinate compression is incomplete.");

            for (int segment = first; segment < last; segment++)
            {
                bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                    access.Mode == GraphAccessMode.Write &&
                    access.Coverage == WriteCoverage.Partial;
                if (reads && analysis.Contents[segment] != ResourceContentState.Defined)
                {
                    throw new UndefinedResourceReadException(
                        $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Query results.");
                }
                if (reads && analysis.Writers[segment] >= 0)
                    AddValueDependency(analysis, access.PassIndex, analysis.Writers[segment]);

                if (access.Mode == GraphAccessMode.Read) continue;
                analysis.Writers[segment] = access.PassIndex;
                if (access.ResultContents.HasValue)
                    analysis.Contents[segment] = access.ResultContents.Value;
                else if (access.Mode == GraphAccessMode.ReadWrite ||
                         access.Coverage == WriteCoverage.Complete)
                    analysis.Contents[segment] = ResourceContentState.Defined;
            }
        }

        for (int segment = 0; segment < segmentCount; segment++)
            if (analysis.Writers[segment] >= 0) AddLiveRoot(analysis, analysis.Writers[segment]);
    }

    private void ResolveShaderTableValues(ResourceAnalysis analysis, int tableIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;

        SortAccessesByValueOrder(analysis, accesses);
        int writer = -1;
        ResourceContentState contents = ResolveShaderTableContents(_shaderTables[tableIndex].EntryBoundaryStates);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                access.Mode == GraphAccessMode.Write &&
                access.Coverage == WriteCoverage.Partial;
            if (reads && contents != ResourceContentState.Defined)
            {
                throw new UndefinedResourceReadException(
                    $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads an undefined RayTracingShaderTable.");
            }
            if (reads && writer >= 0)
                AddValueDependency(analysis, access.PassIndex, writer);

            if (access.Mode == GraphAccessMode.Read) continue;
            writer = access.PassIndex;
            if (access.ResultContents.HasValue)
                contents = access.ResultContents.Value;
            else if (access.Mode == GraphAccessMode.ReadWrite ||
                     access.Coverage == WriteCoverage.Complete)
                contents = ResourceContentState.Defined;
        }

        if (writer >= 0) AddLiveRoot(analysis, writer);
    }

    private void ResolveBufferHazards(ResourceAnalysis analysis, int bufferIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;
        if (OnlyReads(accesses)) return;
        int coordinateCount = BuildBufferCoordinates(analysis, _buffers[bufferIndex], accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref analysis.Writers, segmentCount);
        Array.Fill(analysis.Writers, -1, 0, segmentCount);
        PrepareReaderScratch(analysis, segmentCount);
        PrepareSharedWriterScratch(analysis, segmentCount);
        SortAccessesByValueOrder(analysis, accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (BufferRange range in access.BufferRanges)
            {
                ReadOnlySpan<ulong> coordinates = analysis.BufferCoordinates.AsSpan(0, coordinateCount);
                int first = BinarySearch(coordinates, range.Offset);
                int last = BinarySearch(
                    coordinates,
                    checked(range.Offset + range.Size));
                for (int segment = first; segment < last; segment++)
                {
                    if (access.Mode == GraphAccessMode.Read)
                    {
                        if (analysis.Writers[segment] >= 0)
                            AddEdge(analysis, analysis.Writers[segment], access.PassIndex);
                        foreach (int writer in analysis.SharedWriters[segment])
                            AddEdge(analysis, writer, access.PassIndex);
                        AddUnique(analysis.Readers[segment], access.PassIndex);
                        continue;
                    }

                    if (analysis.Writers[segment] >= 0)
                        AddEdge(analysis, analysis.Writers[segment], access.PassIndex);
                    foreach (int reader in analysis.Readers[segment]) AddEdge(analysis, reader, access.PassIndex);
                    analysis.Readers[segment].Clear();
                    if (access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered)
                    {
                        foreach (int writer in analysis.SharedWriters[segment])
                            if (!CanOverlapWrites(writer, access.PassIndex))
                                AddEdge(analysis, writer, access.PassIndex);
                        AddUnique(analysis.SharedWriters[segment], access.PassIndex);
                        continue;
                    }
                    foreach (int writer in analysis.SharedWriters[segment])
                        AddEdge(analysis, writer, access.PassIndex);
                    analysis.SharedWriters[segment].Clear();
                    analysis.Writers[segment] = access.PassIndex;
                }
            }
        }
    }

    private void ResolveTextureHazards(ResourceAnalysis analysis, int textureIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;
        FrameTexture texture = _textures[textureIndex];
        if (texture.Lifetime == RenderGraphResourceLifetime.Persistent && texture.Definition is not null)
            texture.EntryBoundaryStates = texture.Definition.BoundaryStates;
        else if (texture.Ownership == RenderGraphResourceOwnership.GraphOwned)
            texture.EntryBoundaryStates = null;
        FrameResourceAccess firstAccess = _accesses[accesses[0]];
        bool sameReadLayout = firstAccess.Mode == GraphAccessMode.Read;
        for (int index = 1; sameReadLayout && index < accesses.Count; index++)
        {
            FrameResourceAccess access = _accesses[accesses[index]];
            sameReadLayout = access.Mode == GraphAccessMode.Read &&
                access.TextureLayout == firstAccess.TextureLayout;
        }
        if (sameReadLayout) return;
        int cellCount = BuildTextureCoordinates(analysis, texture, accesses);
        EnsureCapacity(ref analysis.Writers, cellCount);
        Array.Fill(analysis.Writers, -1, 0, cellCount);
        PrepareReaderScratch(analysis, cellCount);
        PrepareSharedWriterScratch(analysis, cellCount);
        EnsureCapacity(ref analysis.Layouts, cellCount);
        Array.Clear(analysis.Layouts, 0, cellCount);
        SortAccessesByValueOrder(analysis, accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (int cell in TextureRegions(analysis, texture, access.TextureRange))
            {
                if (access.Mode == GraphAccessMode.Read)
                {
                    if (analysis.Writers[cell] >= 0) AddEdge(analysis, analysis.Writers[cell], access.PassIndex);
                    foreach (int writer in analysis.SharedWriters[cell])
                        AddEdge(analysis, writer, access.PassIndex);
                    if (analysis.Layouts[cell].HasValue &&
                        analysis.Layouts[cell].GetValueOrDefault() != access.TextureLayout)
                        foreach (int reader in analysis.Readers[cell]) AddEdge(analysis, reader, access.PassIndex);
                    AddUnique(analysis.Readers[cell], access.PassIndex);
                    analysis.Layouts[cell] = access.TextureLayout;
                    continue;
                }

                if (analysis.Writers[cell] >= 0) AddEdge(analysis, analysis.Writers[cell], access.PassIndex);
                foreach (int reader in analysis.Readers[cell]) AddEdge(analysis, reader, access.PassIndex);
                analysis.Readers[cell].Clear();
                if (access.UnorderedAccessHazards != UnorderedAccessHazardMode.Ordered)
                {
                    foreach (int writer in analysis.SharedWriters[cell])
                        if (!CanOverlapWrites(writer, access.PassIndex))
                            AddEdge(analysis, writer, access.PassIndex);
                    AddUnique(analysis.SharedWriters[cell], access.PassIndex);
                    analysis.Layouts[cell] = access.TextureLayout;
                    continue;
                }
                foreach (int writer in analysis.SharedWriters[cell])
                    AddEdge(analysis, writer, access.PassIndex);
                analysis.SharedWriters[cell].Clear();
                analysis.Writers[cell] = access.PassIndex;
                analysis.Layouts[cell] = access.TextureLayout;
            }
        }
    }

    private void ResolveQueryHazards(ResourceAnalysis analysis, int poolIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (accesses.Count == 0) return;
        if (OnlyReads(accesses)) return;

        int coordinateCount = BuildQueryCoordinates(analysis, _queryPools[poolIndex], accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref analysis.Writers, segmentCount);
        Array.Fill(analysis.Writers, -1, 0, segmentCount);
        PrepareReaderScratch(analysis, segmentCount);
        SortAccessesByValueOrder(analysis, accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            ReadOnlySpan<uint> coordinates = analysis.QueryCoordinates.AsSpan(0, coordinateCount);
            int first = BinarySearch(coordinates, access.QueryRange.FirstQuery);
            int last = BinarySearch(
                coordinates,
                checked(access.QueryRange.FirstQuery + access.QueryRange.QueryCount));
            for (int segment = first; segment < last; segment++)
            {
                if (access.Mode == GraphAccessMode.Read)
                {
                    if (analysis.Writers[segment] >= 0)
                        AddEdge(analysis, analysis.Writers[segment], access.PassIndex);
                    AddUnique(analysis.Readers[segment], access.PassIndex);
                    continue;
                }

                if (analysis.Writers[segment] >= 0)
                    AddEdge(analysis, analysis.Writers[segment], access.PassIndex);
                foreach (int reader in analysis.Readers[segment])
                    AddEdge(analysis, reader, access.PassIndex);
                analysis.Readers[segment].Clear();
                analysis.Writers[segment] = access.PassIndex;
            }
        }
    }

    private void ResolveShaderTableHazards(ResourceAnalysis analysis, int tableIndex)
    {
        List<int> accesses = analysis.Accesses;
        if (OnlyReads(accesses)) return;
        SortAccessesByValueOrder(analysis, accesses);

        int writer = -1;
        analysis.ShaderReaders.Clear();
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.Mode == GraphAccessMode.Read)
            {
                if (writer >= 0) AddEdge(analysis, writer, access.PassIndex);
                AddUnique(analysis.ShaderReaders, access.PassIndex);
                continue;
            }

            if (writer >= 0) AddEdge(analysis, writer, access.PassIndex);
            foreach (int reader in analysis.ShaderReaders) AddEdge(analysis, reader, access.PassIndex);
            analysis.ShaderReaders.Clear();
            writer = access.PassIndex;
        }
    }

    private void AddEdge(ResourceAnalysis analysis, int predecessor, int consumer)
    {
        if (predecessor == consumer) return;
        analysis.Dependencies.Hazards.Add(new(predecessor, consumer));
    }

    private List<int> CollectResourceAccesses(
        GraphAccessTargetKind kind,
        int resourceIndex,
        bool enabledOnly,
        List<int>? destination = null)
    {
        destination ??= _resourceAccessScratch;
        destination.Clear();
        if (_resourceAccessLists.TryGetValue((kind, resourceIndex), out List<int>? accesses))
            foreach (int index in accesses)
                if (!enabledOnly || _passes[_accesses[index].PassIndex].Enabled) destination.Add(index);
        return destination;
    }

    private readonly HashSet<int> _orderSearchVisited = [];
    private readonly Stack<int> _orderSearchWork = new();
    private ulong[][] _passOrderAncestors = [];
    private enum PassOrderSearchState : byte { Unvisited, Searching, Complete }
    private PassOrderSearchState[] _passOrderSearchStates = [];
    private int _passOrderWordCount;
    private bool _hasPassOrderCycle;

    private ReadOnlySpan<ulong> CollectPassOrderAncestors(int pass)
    {
        if (_passOrderSearchStates[pass] == PassOrderSearchState.Complete) return _passOrderAncestors[pass];
        _orderSearchWork.Clear();
        _orderSearchWork.Push(pass);
        while (_orderSearchWork.TryPeek(out int current))
        {
            if (_passOrderSearchStates[current] == PassOrderSearchState.Complete)
            {
                _orderSearchWork.Pop();
                continue;
            }
            if (_passOrderSearchStates[current] == PassOrderSearchState.Unvisited)
            {
                _analyzedPasses.Add(current);
                _passOrderSearchStates[current] = PassOrderSearchState.Searching;
                ref ulong[] words = ref _passOrderAncestors[current];
                if (words is null || words.Length < _passOrderWordCount)
                    words = new ulong[_passOrderWordCount];
                else Array.Clear(words, 0, _passOrderWordCount);
                if (_passes[current].Enabled && _orderEdgesByPass.TryGetValue(current, out var edges))
                    foreach (PassEdge edge in edges)
                    {
                        if (edge.Consumer != current || !_passes[edge.Predecessor].Enabled) continue;
                        if (_passOrderSearchStates[edge.Predecessor] == PassOrderSearchState.Searching)
                        {
                            // Resource projection and live-pass scheduling retain cycle validation.
                            _hasPassOrderCycle = true;
                            return [];
                        }
                        if (_passOrderSearchStates[edge.Predecessor] == PassOrderSearchState.Unvisited)
                            _orderSearchWork.Push(edge.Predecessor);
                    }
                if (_orderSearchWork.Peek() != current) continue;
            }
            ulong[] ancestors = _passOrderAncestors[current];
            if (_passes[current].Enabled && _orderEdgesByPass.TryGetValue(current, out var predecessors))
                foreach (PassEdge edge in predecessors)
                {
                    if (edge.Consumer != current || !_passes[edge.Predecessor].Enabled) continue;
                    ulong[] previous = _passOrderAncestors[edge.Predecessor];
                    for (int word = 0; word < _passOrderWordCount; word++)
                        ancestors[word] |= previous[word];
                    ancestors[edge.Predecessor / 64] |= 1UL << (edge.Predecessor & 63);
                }
            _passOrderSearchStates[current] = PassOrderSearchState.Complete;
            _orderSearchWork.Pop();
        }
        return _passOrderAncestors[pass];
    }


    // Explicit-order ancestry is fixed before resource jobs start.
    private IReadOnlyDictionary<int, int> OrderResourceUsers(ResourceAnalysis analysis, List<int> accesses)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(ResourceOrderTrace);
        analysis.Passes.Clear();
        analysis.Ranks.Clear();
        analysis.ReadyPasses.Clear();
        foreach (int access in accesses)
        {
            int node = _accesses[access].PassIndex;
            if (analysis.Ranks.TryAdd(node, analysis.Passes.Count)) analysis.Passes.Add(node);
        }
        int count = analysis.Passes.Count;
        EnsureCapacity(ref analysis.PassWords, _passOrderWordCount);
        Array.Clear(analysis.PassWords, 0, _passOrderWordCount);
        foreach (int node in analysis.Passes)
            analysis.PassWords[node / 64] |= 1UL << (node & 63);
        if (analysis.Indegrees.Length < count) Array.Resize(ref analysis.Indegrees, count);
        if (analysis.Successors.Length < count) Array.Resize(ref analysis.Successors, count);
        Array.Clear(analysis.Indegrees, 0, count);
        for (int index = 0; index < count; index++) (analysis.Successors[index] ??= []).Clear();
        for (int consumer = 0; consumer < count; consumer++)
        {
            ReadOnlySpan<ulong> ancestors = _hasPassOrderCycle
                ? default : _passOrderAncestors[analysis.Passes[consumer]];
            if (_hasPassOrderCycle)
                AddResourceOrderPredecessors(consumer);
            else
            {
                for (int word = 0; word < _passOrderWordCount; word++)
                {
                    ulong users = ancestors[word] & analysis.PassWords[word];
                    while (users != 0)
                    {
                        int node = word * 64 + System.Numerics.BitOperations.TrailingZeroCount(users);
                        users &= users - 1;
                        int predecessor = analysis.Ranks[node];
                        analysis.Successors[predecessor].Add(consumer);
                        analysis.Indegrees[consumer]++;
                    }
                }
            }
            if (analysis.Indegrees[consumer] == 0)
                analysis.ReadyPasses.Enqueue(consumer, _passes[analysis.Passes[consumer]].DeclarationOrdinal);
        }
        int rank = 0;
        while (analysis.ReadyPasses.TryDequeue(out int index, out _))
        {
            analysis.Ranks[analysis.Passes[index]] = rank++;
            foreach (int consumer in analysis.Successors[index])
                if (--analysis.Indegrees[consumer] == 0)
                    analysis.ReadyPasses.Enqueue(consumer, _passes[analysis.Passes[consumer]].DeclarationOrdinal);
        }
        if (rank != count) throw new InvalidOperationException("The pass dependency introduces a cycle.");
        return analysis.Ranks;

        void AddResourceOrderPredecessors(int consumer)
        {
            analysis.VisitedPasses.Clear();
            analysis.PassesToVisit.Clear();
            PushPredecessors(analysis.Passes[consumer]);
            while (analysis.PassesToVisit.TryPop(out int node))
            {
                if (!analysis.VisitedPasses.Add(node)) continue;
                if (analysis.Ranks.TryGetValue(node, out int predecessor))
                {
                    analysis.Successors[predecessor].Add(consumer);
                    analysis.Indegrees[consumer]++;
                }
                else PushPredecessors(node);
            }
        }

        void PushPredecessors(int pass)
        {
            if (!_passes[pass].Enabled || !_orderEdgesByPass.TryGetValue(pass, out var edges)) return;
            foreach (PassEdge edge in edges)
                if (edge.Consumer == pass && _passes[edge.Predecessor].Enabled)
                    analysis.PassesToVisit.Push(edge.Predecessor);
        }
    }

    private bool RequiresValueOrder(List<int> accesses)
    {
        if (accesses.Count < 2) return false;
        FrameResourceAccess first = _accesses[accesses[0]];
        foreach (int index in accesses)
        {
            FrameResourceAccess access = _accesses[index];
            if (access.Mode != GraphAccessMode.Read || access.TextureLayout != first.TextureLayout)
                return true;
        }
        return false;
    }

    private void SortAccessesByValueOrder(ResourceAnalysis analysis, List<int> accesses)
    {
        if (!RequiresValueOrder(accesses)) return;
        OrderResourceUsers(analysis, accesses);
        accesses.Sort(analysis.AccessComparison);
    }

    private Comparison<int>? _accessScheduleComparison;

    private void SortAccessesBySchedule(List<int> accesses)
    {
        // Insertion sort remains cheap for a handful of accesses, but imported
        // scenes can put thousands of users on a shared geometry buffer.
        if (accesses.Count > 32)
        {
            accesses.Sort(_accessScheduleComparison ??= CompareAccessSchedule);
            return;
        }
        for (int index = 1; index < accesses.Count; index++)
        {
            int value = accesses[index];
            long ordinal = _executionOrder.Position(_accesses[value].PassIndex);
            int insertion = index;
            while (insertion > 0 &&
                   _executionOrder.Position(_accesses[accesses[insertion - 1]].PassIndex) > ordinal)
            {
                accesses[insertion] = accesses[insertion - 1];
                insertion--;
            }
            accesses[insertion] = value;
        }
    }

    private int CompareAccessSchedule(int left, int right)
    {
        FrameResourceAccess a = _accesses[left], b = _accesses[right];
        int order = _executionOrder.Position(a.PassIndex).CompareTo(_executionOrder.Position(b.PassIndex));
        return order != 0 ? order : a.PassAdjacencyIndex.CompareTo(b.PassAdjacencyIndex);
    }

    private void FilterLiveAccesses(List<int> accesses)
    {
        int destination = 0;
        for (int source = 0; source < accesses.Count; source++)
        {
            int access = accesses[source];
            if (!_live[_accesses[access].PassIndex]) continue;
            accesses[destination++] = access;
        }
        if (destination < accesses.Count)
            accesses.RemoveRange(destination, accesses.Count - destination);
    }

    private void PrepareReaderScratch(ResourceAnalysis analysis, int count)
    {
        if (analysis.Readers.Length < count)
            EnsureCapacity(ref analysis.Readers, count);
        for (int index = 0; index < count; index++)
            (analysis.Readers[index] ??= []).Clear();
    }

    private int BuildQueryCoordinates(ResourceAnalysis analysis, in FrameQueryPool pool, List<int> accesses)
    {
        int required = checked((accesses.Count + pool.EntryBoundaryStates.Length) * 2 + 2);
        EnsureCapacity(ref analysis.QueryCoordinates, required);
        int count = 0;
        analysis.QueryCoordinates[count++] = 0;
        analysis.QueryCoordinates[count++] = pool.Resource.Description.Count;
        foreach (QueryBoundaryState endpoint in pool.EntryBoundaryStates)
        {
            analysis.QueryCoordinates[count++] = endpoint.Range.FirstQuery;
            analysis.QueryCoordinates[count++] = checked(
                endpoint.Range.FirstQuery + endpoint.Range.QueryCount);
        }
        foreach (int accessIndex in accesses)
        {
            QueryRange range = _accesses[accessIndex].QueryRange;
            analysis.QueryCoordinates[count++] = range.FirstQuery;
            analysis.QueryCoordinates[count++] = checked(range.FirstQuery + range.QueryCount);
        }

        Array.Sort(analysis.QueryCoordinates, 0, count);
        int unique = 0;
        for (int i = 0; i < count; i++)
        {
            if (unique != 0 &&
                analysis.QueryCoordinates[unique - 1] == analysis.QueryCoordinates[i]) continue;
            analysis.QueryCoordinates[unique++] = analysis.QueryCoordinates[i];
        }
        return unique;
    }

    // Whole entries resolve against each descriptor's own view range.

    private int BuildBufferCoordinates(ResourceAnalysis analysis, in FrameBuffer buffer, List<int> accesses)
    {
        int endpointCount = buffer.EntryBoundaryStates?.Length ?? 0;
        analysis.RangeSets.Clear();
        int rangeCount = 0;
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.SharedBufferRanges is null ||
                analysis.RangeSets.Add((access.SharedBufferRanges, access.BufferRange)))
                rangeCount = checked(rangeCount + access.BufferRanges.Count);
        }
        int required = checked((rangeCount + endpointCount) * 2 + 2);
        EnsureCapacity(ref analysis.BufferCoordinates, required);
        int count = 0;
        analysis.BufferCoordinates[count++] = 0;
        analysis.BufferCoordinates[count++] = buffer.Description.Size;
        if (buffer.EntryBoundaryStates is not null)
        {
            foreach (BufferBoundaryState endpoint in buffer.EntryBoundaryStates)
            {
                BufferRange range = GraphStructureIndex.ResolveRange(
                    endpoint.Range,
                    buffer.Description.Size,
                    buffer.Description.Label);
                analysis.BufferCoordinates[count++] = range.Offset;
                analysis.BufferCoordinates[count++] = checked(range.Offset + range.Size);
            }
        }
        analysis.RangeSets.Clear();
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.SharedBufferRanges is not null &&
                !analysis.RangeSets.Add((access.SharedBufferRanges, access.BufferRange))) continue;
            foreach (BufferRange range in access.BufferRanges)
            {
                analysis.BufferCoordinates[count++] = range.Offset;
                analysis.BufferCoordinates[count++] = checked(range.Offset + range.Size);
            }
        }
        Array.Sort(analysis.BufferCoordinates, 0, count);
        int unique = 0;
        for (int i = 0; i < count; i++)
        {
            if (unique != 0 &&
                analysis.BufferCoordinates[unique - 1] == analysis.BufferCoordinates[i]) continue;
            analysis.BufferCoordinates[unique++] = analysis.BufferCoordinates[i];
        }
        return unique;
    }

    private static void InitializeBufferContents(
        in FrameBuffer buffer,
        ReadOnlySpan<ulong> coordinates,
        Span<ResourceContentState> contents,
        Span<BufferContentScope> contentScopes)
    {
        if (buffer.EntryBoundaryStates is null) return;
        foreach (BufferBoundaryState endpoint in buffer.EntryBoundaryStates)
        {
            BufferRange range = GraphStructureIndex.ResolveRange(endpoint.Range, buffer.Description.Size);
            int first = BinarySearch(coordinates, range.Offset);
            int last = BinarySearch(coordinates, checked(range.Offset + range.Size));
            if (first < 0 || last < 0) continue;
            for (int segment = first; segment < last; segment++)
            {
                contents[segment] = endpoint.Contents;
                contentScopes[segment] = endpoint.ContentScope;
            }
        }
    }

    private void PrepareSharedWriterScratch(ResourceAnalysis analysis, int count)
    {
        if (analysis.SharedWriters.Length < count)
            EnsureCapacity(ref analysis.SharedWriters, count);
        for (int index = 0; index < count; index++)
            (analysis.SharedWriters[index] ??= []).Clear();
    }

    private bool CanOverlapWrites(int leftPass, int rightPass)
    {
        FramePass left = _passes[leftPass];
        FramePass right = _passes[rightPass];
        Queue[] leftCandidates = _frame.Graph.ResolveQueueCandidates(left.QueuePolicy, left.Kind);
        Queue[] rightCandidates = _frame.Graph.ResolveQueueCandidates(right.QueuePolicy, right.Kind);
        return leftCandidates.Length == 1 &&
            rightCandidates.Length == 1 &&
            ReferenceEquals(leftCandidates[0], rightCandidates[0]);
    }

    private static bool HasRequiredBufferContents(
        ResourceContentState contents,
        BufferContentScope availableScope,
        BufferContentScope requiredScope) =>
        contents == ResourceContentState.Defined &&
        (availableScope == BufferContentScope.FullRange ||
         availableScope == requiredScope ||
         requiredScope == BufferContentScope.CountBoundedRanges);

    private void InitializeTextureContents(ResourceAnalysis analysis,
        in FrameTexture texture,
        Span<ResourceContentState> contents)
    {
        if (texture.EntryBoundaryStates is null) return;
        foreach (TextureBoundaryState endpoint in texture.EntryBoundaryStates)
            foreach (int cell in TextureRegions(analysis, texture, endpoint.Range))
                contents[cell] = endpoint.Contents;
    }

    private static void InitializeQueryContents(
        in FrameQueryPool pool,
        ReadOnlySpan<uint> coordinates,
        Span<ResourceContentState> contents)
    {
        foreach (QueryBoundaryState endpoint in pool.EntryBoundaryStates)
        {
            int first = BinarySearch(coordinates, endpoint.Range.FirstQuery);
            int last = BinarySearch(
                coordinates,
                checked(endpoint.Range.FirstQuery + endpoint.Range.QueryCount));
            if (first < 0 || last < 0) continue;
            for (int segment = first; segment < last; segment++)
                contents[segment] = endpoint.Contents;
        }
    }

    private static ResourceContentState ResolveShaderTableContents(
        ReadOnlySpan<RayTracingShaderTableBoundaryState> boundaryStates)
    {
        if (boundaryStates.IsEmpty) return ResourceContentState.Undefined;
        ResourceContentState contents = boundaryStates[0].Contents;
        for (int i = 1; i < boundaryStates.Length; i++)
        {
            if (boundaryStates[i].Contents != contents)
            {
                throw new InvalidOperationException(
                    "RayTracingShaderTable reader boundaryStates disagree about contents.");
            }
        }
        return contents;
    }

    private static int TextureAspectCount(Format format)
    {
        TextureAspects aspects = TextureFormatRules.Aspects(format);
        return aspects is TextureAspects.Color or TextureAspects.Depth ? 1 : 2;
    }

    private static TextureAspects TextureAspectAt(Format format, int index)
    {
        TextureAspects aspects = TextureFormatRules.Aspects(format);
        if (aspects == TextureAspects.Color) return TextureAspects.Color;
        if (aspects == TextureAspects.Depth) return TextureAspects.Depth;
        return index == 0 ? TextureAspects.Depth : TextureAspects.Stencil;
    }

    private TextureRegionEnumerable TextureRegions(ResourceAnalysis analysis,
        in FrameTexture texture,
        in TextureSubresourceRange range)
    {
        uint firstMip = checked((uint)BinarySearch(analysis.MipCoordinates.AsSpan(0, analysis.MipCoordinateCount), range.FirstMipLevel));
        uint mipEnd = checked((uint)BinarySearch(analysis.MipCoordinates.AsSpan(0, analysis.MipCoordinateCount), range.FirstMipLevel + range.MipLevelCount));
        uint firstLayer = checked((uint)BinarySearch(analysis.LayerCoordinates.AsSpan(0, analysis.LayerCoordinateCount), range.FirstArrayLayer));
        uint layerEnd = checked((uint)BinarySearch(analysis.LayerCoordinates.AsSpan(0, analysis.LayerCoordinateCount), range.FirstArrayLayer + range.ArrayLayerCount));
        return new TextureRegionEnumerable((uint)(analysis.MipCoordinateCount - 1), (uint)(analysis.LayerCoordinateCount - 1),
            texture.Format, new TextureSubresourceRange(firstMip, mipEnd - firstMip, firstLayer, layerEnd - firstLayer, range.Aspects));
    }

    private readonly struct TextureRegionEnumerable
    {
        private readonly uint _mipCount;
        private readonly uint _layerCount;
        private readonly Format _format;
        private readonly TextureSubresourceRange _range;

        internal TextureRegionEnumerable(
            uint mipCount,
            uint layerCount,
            Format format,
            in TextureSubresourceRange range)
        {
            _mipCount = mipCount;
            _layerCount = layerCount;
            _format = format;
            _range = range;
        }

        public Enumerator GetEnumerator() => new(_mipCount, _layerCount, _format, _range);

        internal struct Enumerator
        {
            private readonly uint _mipCount;
            private readonly uint _layerCount;
            private readonly Format _format;
            private readonly uint _firstMip;
            private readonly uint _mipEnd;
            private readonly uint _firstLayer;
            private readonly uint _layerEnd;
            private readonly TextureAspects _selectedAspects;
            private readonly int _aspectCount;
            private int _aspect;
            private uint _mip;
            private uint _layer;

            internal Enumerator(
                uint mipCount,
                uint layerCount,
                Format format,
                in TextureSubresourceRange range)
            {
                _mipCount = mipCount;
                _layerCount = layerCount;
                _format = format;
                _firstMip = range.FirstMipLevel;
                _mipEnd = checked(range.FirstMipLevel + range.MipLevelCount);
                _firstLayer = range.FirstArrayLayer;
                _layerEnd = checked(range.FirstArrayLayer + range.ArrayLayerCount);
                _selectedAspects = range.Aspects;
                _aspectCount = TextureAspectCount(format);
                _aspect = 0;
                _mip = _firstMip;
                _layer = _firstLayer;
                Current = default;
            }

            public int Current { get; private set; }

            public bool MoveNext()
            {
                while (_aspect < _aspectCount)
                {
                    TextureAspects aspect = TextureAspectAt(_format, _aspect);
                    if ((_selectedAspects & aspect) == 0 || _layer >= _layerEnd)
                    {
                        _aspect++;
                        _mip = _firstMip;
                        _layer = _firstLayer;
                        continue;
                    }

                    Current = checked(
                        (_aspect * (int)_layerCount + (int)_layer) * (int)_mipCount + (int)_mip);
                    _mip++;
                    if (_mip >= _mipEnd)
                    {
                        _mip = _firstMip;
                        _layer++;
                    }
                    return true;
                }
                return false;
            }
        }
    }

    private static void AddUnique(List<int> values, int value)
    {
        // Accesses are grouped by pass after value-order sorting. Repeated
        // accesses by one pass are adjacent for each affected range.
        if (values.Count == 0 || values[^1] != value) values.Add(value);
    }

    private bool OnlyReads(List<int> accesses)
    {
        foreach (int index in accesses)
            if (_accesses[index].Mode != GraphAccessMode.Read) return false;
        return true;
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (values.Length >= count) return;
        int capacity = values.Length == 0 ? 4 : values.Length;
        while (capacity < count) capacity = checked(capacity * 2);
        Array.Resize(ref values, capacity);
    }

    private static int BinarySearch(ReadOnlySpan<ulong> values, ulong value)
    {
        int low = 0;
        int high = values.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            ulong candidate = values[middle];
            if (candidate == value) return middle;
            if (candidate < value) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private static int BinarySearch(ReadOnlySpan<uint> values, uint value)
    {
        int low = 0;
        int high = values.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            uint candidate = values[middle];
            if (candidate == value) return middle;
            if (candidate < value) low = middle + 1;
            else high = middle - 1;
        }
        return ~low;
    }

    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedAnalysisResources = [];
    private readonly HashSet<int> _changedAnalysisPasses = [];
    private readonly HashSet<int> _analyzedPasses = [];
    private bool _analysisChanged = true;
    private bool _analysisReady;
    private bool _reuseAnalysis;

    private bool TryReuseAnalysis() => _analysisReady && !_analysisChanged;

    private void SaveAnalysis()
    {
        _analysisReady = true;
        _analysisChanged = false;
        _changedAnalysisResources.Clear();
        _changedAnalysisPasses.Clear();
    }

    private void ObserveBuffer(int index, ref FrameBuffer buffer)
    {
        FrameBuffer previous = _buffers[index];
        bool changed = previous.Description.Size != buffer.Description.Size || previous.Ownership != buffer.Ownership ||
            previous.Lifetime != buffer.Lifetime;
        ReadOnlySpan<BufferBoundaryState> states = buffer.Lifetime == RenderGraphResourceLifetime.Persistent
            ? buffer.Definition?.BoundaryStates ?? buffer.EntryBoundaryStates
            : buffer.Ownership == RenderGraphResourceOwnership.GraphOwned
                ? default : buffer.EntryBoundaryStates;
        BufferContents[] contents = previous.Contents ?? [];
        // Upload storage is initialized by the CPU and only read by the GPU.
        // Read barriers may split its state ranges without changing its contents.
        int contentCount = buffer.UploadData is null ? states.Length : 1;
        if (contents.Length != contentCount)
        {
            contents = contentCount == 0 ? [] : new BufferContents[contentCount];
            changed = true;
        }
        for (int item = 0; item < contentCount; item++)
        {
            BufferContents value = buffer.UploadData is null
                ? new(states[item].Range, states[item].Contents, states[item].ContentScope)
                : new(new BufferRange(0, buffer.Description.Size), ResourceContentState.Defined, BufferContentScope.FullRange);
            changed |= contents[item] != value;
            contents[item] = value;
        }
        buffer.Contents = contents;
        if (changed) ChangeAnalysisResource(GraphAccessTargetKind.Buffer, index);
    }

    private void ObserveTexture(int index, ref FrameTexture texture)
    {
        FrameTexture previous = _textures[index];
        bool changed = previous.Width != texture.Width || previous.Height != texture.Height || previous.Depth != texture.Depth ||
            previous.MipLevelCount != texture.MipLevelCount || previous.ArrayLayerCount != texture.ArrayLayerCount || previous.SampleCount != texture.SampleCount ||
            previous.Format != texture.Format || previous.Ownership != texture.Ownership || previous.Lifetime != texture.Lifetime;
        ReadOnlySpan<TextureBoundaryState> states = texture.Lifetime == RenderGraphResourceLifetime.Persistent
            ? texture.Definition?.BoundaryStates ?? texture.EntryBoundaryStates
            : texture.Ownership == RenderGraphResourceOwnership.GraphOwned ? default : texture.EntryBoundaryStates;
        TextureContents[] contents = previous.Contents ?? [];
        if (contents.Length != states.Length)
        {
            contents = states.Length == 0 ? [] : new TextureContents[states.Length];
            changed = true;
        }
        for (int item = 0; item < states.Length; item++)
        {
            TextureContents value = new(states[item].Range, states[item].Contents);
            changed |= contents[item] != value;
            contents[item] = value;
        }
        texture.Contents = contents;
        if (changed) ChangeAnalysisResource(GraphAccessTargetKind.Texture, index);
    }

    private void ObservePass(int index, in FramePass pass)
    {
        FramePass previous = _passes[index];
        if (previous.Options.Recording != pass.Options.Recording ||
            previous.Options.EstimatedRecordingCost != pass.Options.EstimatedRecordingCost)
            _recordingConfigurationPasses.Add(index);
        if (previous.Identity.IsValid && previous.Kind == pass.Kind && previous.Enabled == pass.Enabled &&
            previous.QueuePolicy == pass.QueuePolicy && previous.Options == pass.Options &&
            previous.DeclarationOrdinal == pass.DeclarationOrdinal) return;
        MarkPassResources(index, _stateConnectionChanges);
        _analysisChanged = true;
        _changedAnalysisPasses.Add(index);
        _scheduleSeeds.Add(index);
        if (previous.Identity.IsValid && previous.QueuePolicy != pass.QueuePolicy)
        {
            _scheduleSeeds.UnionWith(_predecessors[index]);
            _scheduleSeeds.UnionWith(_successors[index]);
        }
        ChangeRecording(index);
    }

    private void ObserveAccess(int index, in FrameResourceAccess current)
    {
        FrameResourceAccess previous = _accesses[index];
        bool changed = !previous.Connected || previous.PassIndex != current.PassIndex || previous.ResourceIndex != current.ResourceIndex ||
            previous.TargetKind != current.TargetKind || previous.Mode != current.Mode || previous.Coverage != current.Coverage ||
            previous.Sync != current.Sync || previous.Access != current.Access || previous.BufferRange != current.BufferRange ||
            !ReferenceEquals(previous.SharedBufferRanges, current.SharedBufferRanges) ||
            previous.TextureRange != current.TextureRange || previous.QueryRange != current.QueryRange ||
            previous.TextureLayout != current.TextureLayout || previous.ResultContents != current.ResultContents ||
            previous.BufferContentScope != current.BufferContentScope || previous.UnorderedAccessHazards != current.UnorderedAccessHazards;
        if (!changed) return;
        if (previous.Connected) ChangeAnalysisResource(previous.TargetKind, previous.ResourceIndex);
        ChangeAnalysisResource(current.TargetKind, current.ResourceIndex);
    }

    private void ChangeAnalysisResource(GraphAccessTargetKind kind, int index)
    {
        _stateUpdateResourcesChanged.Add((kind, index));
        _analysisChanged = true;
        _changedAnalysisResources.Add((kind, index));
        ChangeSynchronization((kind, index));
        _changedLifetimeResources.Add((kind, index));
    }

    private sealed class ResourceDependencies
    {
        internal HashSet<PassEdge> Values = [];
        internal HashSet<PassEdge> Hazards = [];
        internal HashSet<int> Roots = [];
    }

    private sealed class ResourceAnalysis
    {
        private readonly FrameExecutor _executor;
        internal (GraphAccessTargetKind Kind, int Index) Resource;
        internal ResourceDependencies Dependencies = new();
        internal readonly List<int> Accesses = [];
        internal readonly List<int> ShaderReaders = [];
        internal ulong[] BufferCoordinates = [];
        internal uint[] QueryCoordinates = [];
        internal int[] Writers = [];
        internal ResourceContentState[] Contents = [];
        internal BufferContentScope[] ContentScopes = [];
        internal List<int>[] Readers = [];
        internal List<int>[] SharedWriters = [];
        internal TextureLayout?[] Layouts = [];
        internal readonly List<int> Passes = [];
        internal readonly Dictionary<int, int> Ranks = [];
        internal readonly PriorityQueue<int, long> ReadyPasses = new();
        internal int[] Indegrees = [];
        internal List<int>[] Successors = [];
        internal ulong[] PassWords = [];
        internal readonly HashSet<int> VisitedPasses = [];
        internal readonly Stack<int> PassesToVisit = new();
        internal readonly HashSet<(BufferRangeSet Ranges, BufferRange ViewRange)> RangeSets = [];
        internal uint[] MipCoordinates = [];
        internal uint[] LayerCoordinates = [];
        internal int MipCoordinateCount;
        internal int LayerCoordinateCount;
        internal readonly Comparison<int> AccessComparison;

        internal ResourceAnalysis(FrameExecutor executor)
        {
            _executor = executor;
            AccessComparison = CompareAccesses;
        }

        private int CompareAccesses(int left, int right)
        {
            FrameResourceAccess a = _executor._accesses[left], b = _executor._accesses[right];
            int order = Ranks[a.PassIndex].CompareTo(Ranks[b.PassIndex]);
            return order != 0 ? order : a.PassAdjacencyIndex.CompareTo(b.PassAdjacencyIndex);
        }
    }

    private readonly List<ResourceAnalysis> _resourceAnalysis = [];

    private int PrepareResourceAnalysis(HashSet<(GraphAccessTargetKind Kind, int Index)> resources, bool liveOnly)
    {
        int count = 0;
        foreach (var resource in resources)
        {
            if (liveOnly)
            {
                MaterializeMissingResource(resource);
                if (!_resourceDependencies.ContainsKey(resource)) continue;
            }
            if (count == _resourceAnalysis.Count) _resourceAnalysis.Add(new(this));
            ResourceAnalysis analysis = _resourceAnalysis[count++];
            analysis.Resource = resource;
            analysis.Accesses.Clear();
            if (!ResourceExists(resource)) continue;
            CountAnalyzedResourcePasses(resource);
            CollectResourceAccesses(resource.Kind, resource.Index, enabledOnly: !liveOnly, analysis.Accesses);
            if (liveOnly) FilterLiveAccesses(analysis.Accesses);
            if (!RequiresValueOrder(analysis.Accesses)) continue;
            foreach (int access in analysis.Accesses)
            {
                if (_hasPassOrderCycle) break;
                CollectPassOrderAncestors(_accesses[access].PassIndex);
            }
        }
        return count;
    }

    private readonly struct ResourceValuesJob(FrameExecutor executor) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ResourceAnalysis analysis = executor._resourceAnalysis[index];
            analysis.Dependencies.Values.Clear();
            analysis.Dependencies.Roots.Clear();
            if (executor.ResourceExists(analysis.Resource))
                executor.ResolveResourceValues(analysis, analysis.Resource);
        }
    }

    private readonly struct ResourceHazardsJob(FrameExecutor executor) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ResourceAnalysis analysis = executor._resourceAnalysis[index];
            analysis.Dependencies.Hazards.Clear();
            if (executor.ResourceExists(analysis.Resource))
                executor.ResolveResourceHazards(analysis, analysis.Resource);
        }
    }

    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), ResourceDependencies> _resourceDependencies = [];
    private readonly Dictionary<PassEdge, int> _valueEdgeCounts = [];
    private readonly Dictionary<PassEdge, int> _executionEdgeCounts = [];
    private readonly Dictionary<int, int> _rootCounts = [];
    private List<int>[] _valueSuccessors = [];
    private readonly HashSet<int> _livenessSeeds = [];
    private readonly HashSet<int> _livenessComponent = [];
    private int[] _liveSupport = [];
    private readonly List<int> _livenessPath = [];
    private readonly List<int> _livenessPathOffsets = [];
    private readonly HashSet<int> _livenessVisited = [];
    private readonly HashSet<PassEdge> _changedExecutionEdges = [];
    private readonly HashSet<PassEdge> _activeValueEdges = [];
    private readonly HashSet<PassEdge> _activeOrderEdges = [];
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _changedHazardResources = [];
    private int _dependencyPassCount;

    private void UpdateDependencies()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Dependencies);
        EnsureCapacity(ref _live, _passes.Count);
        EnsureCapacity(ref _valuePredecessors, _passes.Count);
        EnsureCapacity(ref _valueSuccessors, _passes.Count);
        EnsureCapacity(ref _predecessors, _passes.Count);
        EnsureCapacity(ref _successors, _passes.Count);
        for (int pass = _dependencyPassCount; pass < _passes.Count; pass++)
        {
            _valuePredecessors[pass] ??= [];
            _valueSuccessors[pass] ??= [];
            _predecessors[pass] ??= [];
            _successors[pass] ??= [];
            _changedAnalysisPasses.Add(pass);
        }
        for (int pass = _passes.Count; pass < _dependencyPassCount; pass++)
            _changedAnalysisPasses.Add(pass);
        _dependencyPassCount = _passes.Count;

        UpdateOrderEdges();
        _passOrderWordCount = (_passes.Count + 63) / 64;
        EnsureCapacity(ref _passOrderAncestors, _passes.Count);
        EnsureCapacity(ref _passOrderSearchStates, _passes.Count);
        Array.Clear(_passOrderSearchStates, 0, _passes.Count);
        _hasPassOrderCycle = false;
        foreach (int pass in _changedAnalysisPasses)
        {
            if ((uint)pass < (uint)_passes.Count) _analyzedPasses.Add(pass);
            _livenessSeeds.Add(pass);
            MarkPassResources(pass, _changedAnalysisResources);
        }
        _changedHazardResources.UnionWith(_changedAnalysisResources);
        int valueCount = PrepareResourceAnalysis(_changedAnalysisResources, liveOnly: false);
        if (valueCount != 0)
            JobSystem.Current.ExecuteParallel(new ResourceValuesJob(this), valueCount);
        for (int index = 0; index < valueCount; index++)
        {
            ResourceAnalysis analysis = _resourceAnalysis[index];
            var resource = analysis.Resource;
            _resourceDependencies.TryGetValue(resource, out ResourceDependencies? previous);
            if (previous is not null)
            {
                foreach (PassEdge edge in previous.Values)
                    if (!analysis.Dependencies.Values.Contains(edge)) ChangeValueEdge(edge, -1);
                foreach (int root in previous.Roots)
                    if (!analysis.Dependencies.Roots.Contains(root)) ChangeRoot(root, -1);
            }
            foreach (PassEdge edge in analysis.Dependencies.Values)
                if (previous is null || !previous.Values.Contains(edge)) ChangeValueEdge(edge, 1);
            foreach (int root in analysis.Dependencies.Roots)
                if (previous is null || !previous.Roots.Contains(root)) ChangeRoot(root, 1);
            if (previous is null)
            {
                analysis.Dependencies.Hazards.Clear();
                _resourceDependencies.Add(resource, analysis.Dependencies);
                analysis.Dependencies = new();
            }
            else
            {
                (previous.Values, analysis.Dependencies.Values) = (analysis.Dependencies.Values, previous.Values);
                (previous.Roots, analysis.Dependencies.Roots) = (analysis.Dependencies.Roots, previous.Roots);
            }
        }

        UpdateLiveness();
        int hazardCount = PrepareResourceAnalysis(_changedHazardResources, liveOnly: true);
        if (hazardCount != 0)
            JobSystem.Current.ExecuteParallel(new ResourceHazardsJob(this), hazardCount);
        for (int index = 0; index < hazardCount; index++)
        {
            ResourceAnalysis analysis = _resourceAnalysis[index];
            var resource = analysis.Resource;
            ResourceDependencies previous = _resourceDependencies[resource];
            foreach (PassEdge edge in previous.Hazards)
                if (!analysis.Dependencies.Hazards.Contains(edge)) ChangeExecutionEdge(edge, -1);
            foreach (PassEdge edge in analysis.Dependencies.Hazards)
                if (!previous.Hazards.Contains(edge)) ChangeExecutionEdge(edge, 1);
            (previous.Hazards, analysis.Dependencies.Hazards) = (analysis.Dependencies.Hazards, previous.Hazards);
            if (!ResourceExists(resource)) _resourceDependencies.Remove(resource);
        }
        foreach (PassEdge edge in _changedExecutionEdges)
        {
            bool live = IsLive(edge.Predecessor) && IsLive(edge.Consumer);
            SetExecutionSource(_activeValueEdges, edge, live && _valueEdgeCounts.ContainsKey(edge));
            SetExecutionSource(_activeOrderEdges, edge, live && _orderEdgeSources.ContainsKey(edge));
        }
        _changedExecutionEdges.Clear();
        _changedHazardResources.Clear();
        _livenessSeeds.Clear();
    }

    private bool ResourceExists((GraphAccessTargetKind Kind, int Index) resource) => resource.Index >= 0 && resource.Index < (resource.Kind switch
    {
        GraphAccessTargetKind.Buffer => _buffers.Count,
        GraphAccessTargetKind.Texture => _textures.Count,
        GraphAccessTargetKind.QueryPool => _queryPools.Count,
        GraphAccessTargetKind.RayTracingShaderTable => _shaderTables.Count,
        _ => 0,
    });

    private void ResolveResourceValues(ResourceAnalysis analysis, (GraphAccessTargetKind Kind, int Index) resource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(ResourceValuesTrace);
        switch (resource.Kind)
        {
            case GraphAccessTargetKind.Buffer: ResolveBufferValues(analysis, resource.Index); break;
            case GraphAccessTargetKind.Texture: ResolveTextureValues(analysis, resource.Index); break;
            case GraphAccessTargetKind.QueryPool: ResolveQueryValues(analysis, resource.Index); break;
            case GraphAccessTargetKind.RayTracingShaderTable: ResolveShaderTableValues(analysis, resource.Index); break;
        }
    }

    private void ResolveResourceHazards(ResourceAnalysis analysis, (GraphAccessTargetKind Kind, int Index) resource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(ResourceHazardsTrace);
        switch (resource.Kind)
        {
            case GraphAccessTargetKind.Buffer: ResolveBufferHazards(analysis, resource.Index); break;
            case GraphAccessTargetKind.Texture: ResolveTextureHazards(analysis, resource.Index); break;
            case GraphAccessTargetKind.QueryPool: ResolveQueryHazards(analysis, resource.Index); break;
            case GraphAccessTargetKind.RayTracingShaderTable: ResolveShaderTableHazards(analysis, resource.Index); break;
        }
    }

    private void CountAnalyzedResourcePasses((GraphAccessTargetKind Kind, int Index) resource)
    {
        if (_resourceAccessLists.TryGetValue(resource, out List<int>? accesses))
            foreach (int index in accesses)
                _analyzedPasses.Add(_accesses[index].PassIndex);
    }

    private void AddValueDependency(ResourceAnalysis analysis, int consumer, int predecessor)
    {
        if (consumer != predecessor) analysis.Dependencies.Values.Add(new(predecessor, consumer));
    }

    private static void AddLiveRoot(ResourceAnalysis analysis, int pass) => analysis.Dependencies.Roots.Add(pass);
    private void ChangeValueEdge(PassEdge edge, int delta)
    {
        int previous = _valueEdgeCounts.GetValueOrDefault(edge);
        int count = previous + delta;
        if (count == 0)
        {
            _valueEdgeCounts.Remove(edge);
            _valuePredecessors[edge.Consumer].Remove(edge.Predecessor);
            _valueSuccessors[edge.Predecessor].Remove(edge.Consumer);
        }
        else
        {
            _valueEdgeCounts[edge] = count;
            if (previous == 0)
            {
                _valuePredecessors[edge.Consumer].Add(edge.Predecessor);
                _valueSuccessors[edge.Predecessor].Add(edge.Consumer);
            }
        }
        if (previous == 0 || count == 0)
        {
            _livenessSeeds.Add(edge.Predecessor);
            _changedExecutionEdges.Add(edge);
        }
    }

    private void ChangeRoot(int pass, int delta)
    {
        int previous = _rootCounts.GetValueOrDefault(pass);
        int count = previous + delta;
        if (count == 0) _rootCounts.Remove(pass);
        else _rootCounts[pass] = count;
        if (previous == 0 || count == 0) _livenessSeeds.Add(pass);
    }

    private void MarkPassResources(int pass, HashSet<(GraphAccessTargetKind Kind, int Index)> resources)
    {
        if (pass >= _passAccesses.Length || _passAccesses[pass] is not { } accesses) return;
        foreach (int index in accesses)
        {
            FrameResourceAccess access = _accesses[index];
            resources.Add((access.TargetKind, access.ResourceIndex));
        }
    }

    private bool IsLive(int pass) => pass < _passes.Count && _live[pass];

    private void UpdateLiveness()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(LivenessTrace);
        int previousCapacity = _liveSupport.Length;
        EnsureCapacity(ref _liveSupport, _live.Length);
        Array.Fill(_liveSupport, -1, previousCapacity, _liveSupport.Length - previousCapacity);
        _livenessComponent.Clear();
        _livenessStack.Clear();
        foreach (int seed in _livenessSeeds)
            if (_livenessComponent.Add(seed)) _livenessStack.Push(seed);
        bool disableCulling = (_frame.Options.Debug & RenderGraphDebugOptions.DisableCulling) != 0;
        while (_livenessStack.TryPop(out int pass))
        {
            _livenessComponent.Remove(pass);
            if (FindLivePath(pass))
            {
                for (int index = _livenessPath.Count - 1; index >= 0; index--)
                {
                    int node = _livenessPath[index];
                    _liveSupport[node] = index + 1 < _livenessPath.Count ? _livenessPath[index + 1] : -1;
                    SetLive(node, true);
                }
            }
            else
            {
                _liveSupport[pass] = -1;
                SetLive(pass, false);
            }
        }

        bool FindLivePath(int seed)
        {
            _livenessPath.Clear();
            _livenessPathOffsets.Clear();
            _livenessVisited.Clear();
            if ((uint)seed >= (uint)_passes.Count || !_passes[seed].Enabled) return false;
            Push(seed);
            while (_livenessPath.Count != 0)
            {
                int last = _livenessPath.Count - 1;
                int node = _livenessPath[last];
                if (disableCulling || _passes[node].Options.Culling == PassCullingMode.NeverCull ||
                    _rootCounts.ContainsKey(node)) return true;
                int offset = _livenessPathOffsets[last];
                List<int> consumers = _valueSuccessors[node];
                if (offset > consumers.Count)
                {
                    _livenessPath.RemoveAt(last);
                    _livenessPathOffsets.RemoveAt(last);
                    continue;
                }
                _livenessPathOffsets[last] = offset + 1;
                int consumer = offset == 0 ? _liveSupport[node] : consumers[offset - 1];
                if (consumer < 0 || (uint)consumer >= (uint)_passes.Count || !_passes[consumer].Enabled ||
                    offset == 0 && !_valueEdgeCounts.ContainsKey(new(node, consumer)) ||
                    _livenessVisited.Contains(consumer)) continue;
                Push(consumer);
            }
            return false;
        }

        void Push(int pass)
        {
            _analyzedPasses.Add(pass);
            _livenessVisited.Add(pass);
            _livenessPath.Add(pass);
            _livenessPathOffsets.Add(0);
        }

        void SetLive(int pass, bool live)
        {
            if (_live[pass] == live) return;
            _live[pass] = live;
            _scheduleSeeds.Add(pass);
            ChangeRecording(pass);
            MarkPassResources(pass, _changedLifetimeResources);
            MarkPassResources(pass, _changedHazardResources);
            foreach (int predecessor in _valuePredecessors[pass]) _changedExecutionEdges.Add(new(predecessor, pass));
            foreach (int consumer in _valueSuccessors[pass]) _changedExecutionEdges.Add(new(pass, consumer));
            if (_orderEdgesByPass.TryGetValue(pass, out HashSet<PassEdge>? orders)) _changedExecutionEdges.UnionWith(orders);
            foreach (int predecessor in _valuePredecessors[pass])
                if (_livenessComponent.Add(predecessor)) _livenessStack.Push(predecessor);
        }
    }

    private void SetExecutionSource(HashSet<PassEdge> source, PassEdge edge, bool present)
    {
        if (present ? source.Add(edge) : source.Remove(edge)) ChangeExecutionEdge(edge, present ? 1 : -1);
    }

    private void ChangeExecutionEdge(PassEdge edge, int delta)
    {
        int previous = _executionEdgeCounts.GetValueOrDefault(edge);
        int count = previous + delta;
        if (previous == 0 || count == 0)
        {
            _scheduleSeeds.Add(edge.Predecessor);
            _scheduleSeeds.Add(edge.Consumer);
            _frontierSeeds.Add(edge.Consumer);
            ChangeRecording(edge.Consumer);
        }
        if (count == 0)
        {
            _executionEdgeCounts.Remove(edge);
            _predecessors[edge.Consumer].Remove(edge.Predecessor);
            _successors[edge.Predecessor].Remove(edge.Consumer);
        }
        else
        {
            _executionEdgeCounts[edge] = count;
            if (previous == 0)
            {
                _predecessors[edge.Consumer].Add(edge.Predecessor);
                _successors[edge.Predecessor].Add(edge.Consumer);
            }
        }
    }


    private int BuildTextureCoordinates(ResourceAnalysis analysis, in FrameTexture texture, List<int> accesses)
    {
        int required = checked((accesses.Count + (texture.EntryBoundaryStates?.Length ?? 0)) * 2 + 2);
        EnsureCapacity(ref analysis.MipCoordinates, required);
        EnsureCapacity(ref analysis.LayerCoordinates, required);
        analysis.MipCoordinates[0] = analysis.LayerCoordinates[0] = 0;
        analysis.MipCoordinates[1] = texture.MipLevelCount;
        analysis.LayerCoordinates[1] = texture.ArrayLayerCount;
        int count = 2;
        if (texture.EntryBoundaryStates is not null)
            foreach (TextureBoundaryState endpoint in texture.EntryBoundaryStates) Add(endpoint.Range);
        foreach (int index in accesses) Add(_accesses[index].TextureRange);
        analysis.MipCoordinateCount = Unique(analysis.MipCoordinates, count);
        analysis.LayerCoordinateCount = Unique(analysis.LayerCoordinates, count);
        return checked((analysis.MipCoordinateCount - 1) * (analysis.LayerCoordinateCount - 1) * TextureAspectCount(texture.Format));

        void Add(in TextureSubresourceRange range)
        {
            analysis.MipCoordinates[count] = range.FirstMipLevel;
            analysis.LayerCoordinates[count++] = range.FirstArrayLayer;
            analysis.MipCoordinates[count] = checked(range.FirstMipLevel + range.MipLevelCount);
            analysis.LayerCoordinates[count++] = checked(range.FirstArrayLayer + range.ArrayLayerCount);
        }

        static int Unique(uint[] coordinates, int count)
        {
            Array.Sort(coordinates, 0, count);
            int unique = 1;
            for (int index = 1; index < count; index++)
                if (coordinates[index] != coordinates[unique - 1]) coordinates[unique++] = coordinates[index];
            return unique;
        }
    }
}
