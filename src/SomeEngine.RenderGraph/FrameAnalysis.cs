namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void ResolveValues()
    {
        for (int pass = 0; pass < _passes.Length; pass++)
        {
            if (_passes[pass].Enabled && _passes[pass].Options.Culling == PassCullingMode.NeverCull)
                _live[pass] = true;
        }

        for (int buffer = 0; buffer < _buffers.Length; buffer++)
            ResolveBufferValues(buffer);
        for (int texture = 0; texture < _textures.Length; texture++)
            ResolveTextureValues(texture);
        for (int pool = 0; pool < _queryPools.Length; pool++)
            ResolveQueryValues(pool);
        for (int table = 0; table < _shaderTables.Length; table++)
            ResolveShaderTableValues(table);
    }

    private void ResolveBufferValues(int bufferIndex)
    {
        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Buffer, bufferIndex, enabledOnly: true);
        if (accesses.Count == 0) return;

        FrameBuffer buffer = _buffers[bufferIndex];
        int coordinateCount = BuildBufferCoordinates(buffer, accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref _writerScratch, segmentCount);
        EnsureCapacity(ref _contentsScratch, segmentCount);
        Array.Fill(_writerScratch, -1, 0, segmentCount);
        Array.Clear(_contentsScratch, 0, segmentCount);
        InitializeBufferContents(
            buffer,
            _bufferCoordinateScratch.AsSpan(0, coordinateCount),
            _contentsScratch.AsSpan(0, segmentCount));

        SortAccessesByDeclaration(accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            int first = BinarySearch(
                _bufferCoordinateScratch.AsSpan(0, coordinateCount),
                access.BufferRange.Offset);
            int last = BinarySearch(
                _bufferCoordinateScratch.AsSpan(0, coordinateCount),
                checked(access.BufferRange.Offset + access.BufferRange.Size));
            if (first < 0 || last < 0)
                throw new InvalidOperationException("RG9001: Buffer coordinate compression is incomplete.");
            for (int segment = first; segment < last; segment++)
            {
                bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                    access.Mode == GraphAccessMode.Write && access.Coverage == WriteCoverage.Partial;
                if (access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite &&
                    _contentsScratch[segment] != ResourceContentState.Defined)
                {
                    throw new InvalidOperationException(
                        $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Buffer contents.");
                }
                if (reads && _writerScratch[segment] >= 0)
                    AddUnique(_valuePredecessors[access.PassIndex], _writerScratch[segment]);

                if (access.Mode != GraphAccessMode.Read)
                {
                    _writerScratch[segment] = access.PassIndex;
                    if (access.ResultContents.HasValue)
                    {
                        _contentsScratch[segment] = access.ResultContents.Value;
                    }
                    else if (access.Mode == GraphAccessMode.ReadWrite ||
                        access.Coverage == WriteCoverage.Complete)
                    {
                        _contentsScratch[segment] = ResourceContentState.Defined;
                    }
                }
            }
        }

        if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent ||
            buffer.Ownership == RenderGraphResourceOwnership.CallerOwned)
        {
            for (int segment = 0; segment < segmentCount; segment++)
                if (_writerScratch[segment] >= 0) _live[_writerScratch[segment]] = true;
        }
    }

    private void ResolveTextureValues(int textureIndex)
    {
        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Texture, textureIndex, enabledOnly: true);
        if (accesses.Count == 0) return;

        FrameTexture texture = _textures[textureIndex];
        int cellCount = TextureCellCount(texture);
        EnsureCapacity(ref _writerScratch, cellCount);
        EnsureCapacity(ref _contentsScratch, cellCount);
        Array.Fill(_writerScratch, -1, 0, cellCount);
        Array.Clear(_contentsScratch, 0, cellCount);
        InitializeTextureContents(texture, _contentsScratch.AsSpan(0, cellCount));

        SortAccessesByDeclaration(accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (int cell in TextureCells(texture, access.TextureRange))
            {
                bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                    access.Mode == GraphAccessMode.Write && access.Coverage == WriteCoverage.Partial;
                if (access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite &&
                    _contentsScratch[cell] != ResourceContentState.Defined)
                {
                    throw new InvalidOperationException(
                        $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Texture contents.");
                }
                if (reads && _writerScratch[cell] >= 0)
                    AddUnique(_valuePredecessors[access.PassIndex], _writerScratch[cell]);
                if (access.Mode != GraphAccessMode.Read)
                {
                    _writerScratch[cell] = access.PassIndex;
                    if (access.ResultContents.HasValue)
                    {
                        _contentsScratch[cell] = access.ResultContents.Value;
                    }
                    else if (access.Mode == GraphAccessMode.ReadWrite ||
                        access.Coverage == WriteCoverage.Complete)
                    {
                        _contentsScratch[cell] = ResourceContentState.Defined;
                    }
                }
            }
        }

        if (texture.Lifetime == RenderGraphResourceLifetime.Persistent ||
            texture.Ownership == RenderGraphResourceOwnership.CallerOwned)
        {
            for (int cell = 0; cell < cellCount; cell++)
                if (_writerScratch[cell] >= 0) _live[_writerScratch[cell]] = true;
        }
    }

    private void ResolveQueryValues(int poolIndex)
    {
        List<int> accesses = CollectResourceAccesses(
            GraphAccessTargetKind.QueryPool,
            poolIndex,
            enabledOnly: true);
        if (accesses.Count == 0) return;

        FrameQueryPool pool = _queryPools[poolIndex];
        int coordinateCount = BuildQueryCoordinates(pool, accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref _writerScratch, segmentCount);
        EnsureCapacity(ref _contentsScratch, segmentCount);
        Array.Fill(_writerScratch, -1, 0, segmentCount);
        Array.Clear(_contentsScratch, 0, segmentCount);
        InitializeQueryContents(
            pool,
            _queryCoordinateScratch.AsSpan(0, coordinateCount),
            _contentsScratch.AsSpan(0, segmentCount));

        SortAccessesByDeclaration(accesses);
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            int first = BinarySearch(
                _queryCoordinateScratch.AsSpan(0, coordinateCount),
                access.QueryRange.FirstQuery);
            int last = BinarySearch(
                _queryCoordinateScratch.AsSpan(0, coordinateCount),
                checked(access.QueryRange.FirstQuery + access.QueryRange.QueryCount));
            if (first < 0 || last < 0)
                throw new InvalidOperationException("RG9001: Query coordinate compression is incomplete.");

            for (int segment = first; segment < last; segment++)
            {
                bool reads = access.Mode is GraphAccessMode.Read or GraphAccessMode.ReadWrite ||
                    access.Mode == GraphAccessMode.Write &&
                    access.Coverage == WriteCoverage.Partial;
                if (reads && _contentsScratch[segment] != ResourceContentState.Defined)
                {
                    throw new InvalidOperationException(
                        $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads undefined Query results.");
                }
                if (reads && _writerScratch[segment] >= 0)
                    AddUnique(_valuePredecessors[access.PassIndex], _writerScratch[segment]);

                if (access.Mode == GraphAccessMode.Read) continue;
                _writerScratch[segment] = access.PassIndex;
                if (access.ResultContents.HasValue)
                    _contentsScratch[segment] = access.ResultContents.Value;
                else if (access.Mode == GraphAccessMode.ReadWrite ||
                         access.Coverage == WriteCoverage.Complete)
                    _contentsScratch[segment] = ResourceContentState.Defined;
            }
        }

        for (int segment = 0; segment < segmentCount; segment++)
            if (_writerScratch[segment] >= 0) _live[_writerScratch[segment]] = true;
    }

    private void ResolveShaderTableValues(int tableIndex)
    {
        List<int> accesses = CollectResourceAccesses(
            GraphAccessTargetKind.RayTracingShaderTable,
            tableIndex,
            enabledOnly: true);
        if (accesses.Count == 0) return;

        SortAccessesByDeclaration(accesses);
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
                throw new InvalidOperationException(
                    $"RG4001: Pass '{_passes[access.PassIndex].Label}' reads an undefined RayTracingShaderTable.");
            }
            if (reads && writer >= 0)
                AddUnique(_valuePredecessors[access.PassIndex], writer);

            if (access.Mode == GraphAccessMode.Read) continue;
            writer = access.PassIndex;
            if (access.ResultContents.HasValue)
                contents = access.ResultContents.Value;
            else if (access.Mode == GraphAccessMode.ReadWrite ||
                     access.Coverage == WriteCoverage.Complete)
                contents = ResourceContentState.Defined;
        }

        if (writer >= 0) _live[writer] = true;
    }

    private void ResolveLiveness()
    {
        if ((_frame.Options.Debug & RenderGraphDebugOptions.DisableCulling) != 0)
        {
            for (int i = 0; i < _passes.Length; i++) _live[i] = _passes[i].Enabled;
            return;
        }

        _livenessStack.Clear();
        for (int i = 0; i < _live.Length; i++)
            if (_live[i] && _passes[i].Enabled) _livenessStack.Push(i);
        while (_livenessStack.TryPop(out int consumer))
        {
            foreach (int producer in _valuePredecessors[consumer])
            {
                if (_live[producer] || !_passes[producer].Enabled) continue;
                _live[producer] = true;
                _livenessStack.Push(producer);
            }
        }
    }

    private void ResolveHazards()
    {
        for (int pass = 0; pass < _passes.Length; pass++)
            if (_live[pass])
                foreach (int producer in _valuePredecessors[pass])
                    if (_live[producer]) AddEdge(producer, pass);

        for (int buffer = 0; buffer < _buffers.Length; buffer++)
            ResolveBufferHazards(buffer);
        for (int texture = 0; texture < _textures.Length; texture++)
            ResolveTextureHazards(texture);
        for (int pool = 0; pool < _queryPools.Length; pool++)
            ResolveQueryHazards(pool);
        for (int table = 0; table < _shaderTables.Length; table++)
            ResolveShaderTableHazards(table);

        GraphStructureIndex structureIndex = _frame.StructureIndex;
        for (int staticConsumer = 0;
             staticConsumer < structureIndex.ExplicitPredecessors.Length;
             staticConsumer++)
        {
            int consumer = ResolvePass(structureIndex.PassIds[staticConsumer]);
            foreach (int staticPredecessor in structureIndex.ExplicitPredecessors[staticConsumer])
            {
                int predecessor = ResolvePass(structureIndex.PassIds[staticPredecessor]);
                if (_live[predecessor] && _live[consumer]) AddEdge(predecessor, consumer);
            }
        }
        foreach (ExplicitPassOrder order in _frame.DynamicOrders)
        {
            int predecessor = ResolvePass(order.Predecessor);
            int consumer = ResolvePass(order.Consumer);
            if (_live[predecessor] && _live[consumer]) AddEdge(predecessor, consumer);
        }

        for (int fixedPass = 0; fixedPass < _passes.Length; fixedPass++)
        {
            if (!_live[fixedPass] || _passes[fixedPass].Options.Scheduling != PassSchedulingMode.PreserveDeclarationPosition)
                continue;
            for (int pass = 0; pass < fixedPass; pass++)
                if (_live[pass]) AddEdge(pass, fixedPass);
            for (int pass = fixedPass + 1; pass < _passes.Length; pass++)
                if (_live[pass]) AddEdge(fixedPass, pass);
        }

        ValidateLiveAcyclic();
    }

    private void ResolveBufferHazards(int bufferIndex)
    {
        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Buffer, bufferIndex, enabledOnly: false);
        FilterLiveAccesses(accesses);
        if (accesses.Count == 0) return;
        int coordinateCount = BuildBufferCoordinates(_buffers[bufferIndex], accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref _writerScratch, segmentCount);
        Array.Fill(_writerScratch, -1, 0, segmentCount);
        PrepareReaderScratch(segmentCount);
        SortAccessesByDeclaration(accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            ReadOnlySpan<ulong> coordinates = _bufferCoordinateScratch.AsSpan(0, coordinateCount);
            int first = BinarySearch(coordinates, access.BufferRange.Offset);
            int last = BinarySearch(
                coordinates,
                checked(access.BufferRange.Offset + access.BufferRange.Size));
            for (int segment = first; segment < last; segment++)
            {
                if (access.Mode == GraphAccessMode.Read)
                {
                    if (_writerScratch[segment] >= 0)
                        AddEdge(_writerScratch[segment], access.PassIndex);
                    AddUnique(_readerScratch[segment], access.PassIndex);
                    continue;
                }

                if (_writerScratch[segment] >= 0)
                    AddEdge(_writerScratch[segment], access.PassIndex);
                foreach (int reader in _readerScratch[segment]) AddEdge(reader, access.PassIndex);
                _readerScratch[segment].Clear();
                _writerScratch[segment] = access.PassIndex;
            }
        }
    }

    private void ResolveTextureHazards(int textureIndex)
    {
        List<int> accesses = CollectResourceAccesses(GraphAccessTargetKind.Texture, textureIndex, enabledOnly: false);
        FilterLiveAccesses(accesses);
        if (accesses.Count == 0) return;
        FrameTexture texture = _textures[textureIndex];
        int cellCount = TextureCellCount(texture);
        EnsureCapacity(ref _writerScratch, cellCount);
        Array.Fill(_writerScratch, -1, 0, cellCount);
        PrepareReaderScratch(cellCount);
        EnsureCapacity(ref _layoutScratch, cellCount);
        Array.Clear(_layoutScratch, 0, cellCount);
        SortAccessesByDeclaration(accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (int cell in TextureCells(texture, access.TextureRange))
            {
                if (access.Mode == GraphAccessMode.Read)
                {
                    if (_writerScratch[cell] >= 0) AddEdge(_writerScratch[cell], access.PassIndex);
                    if (_layoutScratch[cell].HasValue &&
                        _layoutScratch[cell].GetValueOrDefault() != access.TextureLayout)
                        foreach (int reader in _readerScratch[cell]) AddEdge(reader, access.PassIndex);
                    AddUnique(_readerScratch[cell], access.PassIndex);
                    _layoutScratch[cell] = access.TextureLayout;
                    continue;
                }

                if (_writerScratch[cell] >= 0) AddEdge(_writerScratch[cell], access.PassIndex);
                foreach (int reader in _readerScratch[cell]) AddEdge(reader, access.PassIndex);
                _readerScratch[cell].Clear();
                _writerScratch[cell] = access.PassIndex;
                _layoutScratch[cell] = access.TextureLayout;
            }
        }
    }

    private void ResolveQueryHazards(int poolIndex)
    {
        List<int> accesses = CollectResourceAccesses(
            GraphAccessTargetKind.QueryPool,
            poolIndex,
            enabledOnly: false);
        FilterLiveAccesses(accesses);
        if (accesses.Count == 0) return;

        int coordinateCount = BuildQueryCoordinates(_queryPools[poolIndex], accesses);
        int segmentCount = coordinateCount - 1;
        EnsureCapacity(ref _writerScratch, segmentCount);
        Array.Fill(_writerScratch, -1, 0, segmentCount);
        PrepareReaderScratch(segmentCount);
        SortAccessesByDeclaration(accesses);

        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            ReadOnlySpan<uint> coordinates = _queryCoordinateScratch.AsSpan(0, coordinateCount);
            int first = BinarySearch(coordinates, access.QueryRange.FirstQuery);
            int last = BinarySearch(
                coordinates,
                checked(access.QueryRange.FirstQuery + access.QueryRange.QueryCount));
            for (int segment = first; segment < last; segment++)
            {
                if (access.Mode == GraphAccessMode.Read)
                {
                    if (_writerScratch[segment] >= 0)
                        AddEdge(_writerScratch[segment], access.PassIndex);
                    AddUnique(_readerScratch[segment], access.PassIndex);
                    continue;
                }

                if (_writerScratch[segment] >= 0)
                    AddEdge(_writerScratch[segment], access.PassIndex);
                foreach (int reader in _readerScratch[segment])
                    AddEdge(reader, access.PassIndex);
                _readerScratch[segment].Clear();
                _writerScratch[segment] = access.PassIndex;
            }
        }
    }

    private void ResolveShaderTableHazards(int tableIndex)
    {
        List<int> accesses = CollectResourceAccesses(
            GraphAccessTargetKind.RayTracingShaderTable,
            tableIndex,
            enabledOnly: false);
        FilterLiveAccesses(accesses);
        SortAccessesByDeclaration(accesses);

        int writer = -1;
        _shaderReaderScratch.Clear();
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.Mode == GraphAccessMode.Read)
            {
                if (writer >= 0) AddEdge(writer, access.PassIndex);
                AddUnique(_shaderReaderScratch, access.PassIndex);
                continue;
            }

            if (writer >= 0) AddEdge(writer, access.PassIndex);
            foreach (int reader in _shaderReaderScratch) AddEdge(reader, access.PassIndex);
            _shaderReaderScratch.Clear();
            writer = access.PassIndex;
        }
    }

    private void AddEdge(int predecessor, int consumer)
    {
        if (predecessor == consumer) return;
        if (_predecessors[consumer].Contains(predecessor)) return;
        _predecessors[consumer].Add(predecessor);
        _successors[predecessor].Add(consumer);
    }

    private void ValidateLiveAcyclic()
    {
        EnsureCapacity(ref _cycleIndegree, _passes.Length);
        Array.Clear(_cycleIndegree, 0, _passes.Length);
        _cycleReady.Clear();
        int liveCount = 0;
        for (int pass = 0; pass < _passes.Length; pass++)
        {
            if (!_live[pass]) continue;
            liveCount++;
            int count = 0;
            foreach (int predecessor in _predecessors[pass])
                if (_live[predecessor]) count++;
            _cycleIndegree[pass] = count;
            if (count == 0) _cycleReady.Enqueue(pass, _passes[pass].DeclarationOrdinal);
        }
        int visited = 0;
        while (_cycleReady.TryDequeue(out int pass, out _))
        {
            visited++;
            foreach (int successor in _successors[pass])
                if (_live[successor] && --_cycleIndegree[successor] == 0)
                    _cycleReady.Enqueue(successor, _passes[successor].DeclarationOrdinal);
        }
        if (visited != liveCount)
            throw new InvalidOperationException("RG5001: The live render graph contains a dependency cycle.");
    }

    private List<int> CollectResourceAccesses(
        GraphAccessTargetKind kind,
        int resourceIndex,
        bool enabledOnly)
    {
        _resourceAccessScratch.Clear();
        for (int i = 0; i < _accesses.Length; i++)
        {
            FrameResourceAccess access = _accesses[i];
            if (access.TargetKind != kind || access.ResourceIndex != resourceIndex) continue;
            if (enabledOnly && !_passes[access.PassIndex].Enabled) continue;
            _resourceAccessScratch.Add(i);
        }
        return _resourceAccessScratch;
    }

    private void SortAccessesByDeclaration(List<int> accesses)
    {
        for (int index = 1; index < accesses.Count; index++)
        {
            int value = accesses[index];
            int ordinal = _passes[_accesses[value].PassIndex].DeclarationOrdinal;
            int insertion = index;
            while (insertion > 0 &&
                   _passes[_accesses[accesses[insertion - 1]].PassIndex].DeclarationOrdinal > ordinal)
            {
                accesses[insertion] = accesses[insertion - 1];
                insertion--;
            }
            accesses[insertion] = value;
        }
    }

    private void SortAccessesBySchedule(List<int> accesses)
    {
        for (int index = 1; index < accesses.Count; index++)
        {
            int value = accesses[index];
            int ordinal = _scheduledPosition[_accesses[value].PassIndex];
            int insertion = index;
            while (insertion > 0 &&
                   _scheduledPosition[_accesses[accesses[insertion - 1]].PassIndex] > ordinal)
            {
                accesses[insertion] = accesses[insertion - 1];
                insertion--;
            }
            accesses[insertion] = value;
        }
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

    private void PrepareReaderScratch(int count)
    {
        if (_readerScratch.Length < count)
            EnsureCapacity(ref _readerScratch, count);
        for (int index = 0; index < count; index++)
            (_readerScratch[index] ??= []).Clear();
    }

    private int BuildQueryCoordinates(in FrameQueryPool pool, List<int> accesses)
    {
        int required = checked((accesses.Count + pool.EntryBoundaryStates.Length) * 2 + 2);
        EnsureCapacity(ref _queryCoordinateScratch, required);
        int count = 0;
        _queryCoordinateScratch[count++] = 0;
        _queryCoordinateScratch[count++] = pool.Resource.Description.Count;
        foreach (QueryBoundaryState endpoint in pool.EntryBoundaryStates)
        {
            _queryCoordinateScratch[count++] = endpoint.Range.FirstQuery;
            _queryCoordinateScratch[count++] = checked(
                endpoint.Range.FirstQuery + endpoint.Range.QueryCount);
        }
        foreach (int accessIndex in accesses)
        {
            QueryRange range = _accesses[accessIndex].QueryRange;
            _queryCoordinateScratch[count++] = range.FirstQuery;
            _queryCoordinateScratch[count++] = checked(range.FirstQuery + range.QueryCount);
        }

        Array.Sort(_queryCoordinateScratch, 0, count);
        int unique = 0;
        for (int i = 0; i < count; i++)
        {
            if (unique != 0 &&
                _queryCoordinateScratch[unique - 1] == _queryCoordinateScratch[i]) continue;
            _queryCoordinateScratch[unique++] = _queryCoordinateScratch[i];
        }
        return unique;
    }

    private int BuildBufferCoordinates(in FrameBuffer buffer, List<int> accesses)
    {
        int endpointCount = buffer.EntryBoundaryStates?.Length ?? 0;
        int required = checked((accesses.Count + endpointCount) * 2 + 2);
        EnsureCapacity(ref _bufferCoordinateScratch, required);
        int count = 0;
        _bufferCoordinateScratch[count++] = 0;
        _bufferCoordinateScratch[count++] = buffer.Description.Size;
        if (buffer.EntryBoundaryStates is not null)
        {
            foreach (BufferBoundaryState endpoint in buffer.EntryBoundaryStates)
            {
                BufferRange range = GraphStructureIndex.ResolveRange(
                    endpoint.Range,
                    buffer.Description.Size);
                _bufferCoordinateScratch[count++] = range.Offset;
                _bufferCoordinateScratch[count++] = checked(range.Offset + range.Size);
            }
        }
        foreach (int accessIndex in accesses)
        {
            BufferRange range = _accesses[accessIndex].BufferRange;
            _bufferCoordinateScratch[count++] = range.Offset;
            _bufferCoordinateScratch[count++] = checked(range.Offset + range.Size);
        }
        Array.Sort(_bufferCoordinateScratch, 0, count);
        int unique = 0;
        for (int i = 0; i < count; i++)
        {
            if (unique != 0 &&
                _bufferCoordinateScratch[unique - 1] == _bufferCoordinateScratch[i]) continue;
            _bufferCoordinateScratch[unique++] = _bufferCoordinateScratch[i];
        }
        return unique;
    }

    private static void InitializeBufferContents(
        in FrameBuffer buffer,
        ReadOnlySpan<ulong> coordinates,
        Span<ResourceContentState> contents)
    {
        if (buffer.EntryBoundaryStates is null) return;
        foreach (BufferBoundaryState endpoint in buffer.EntryBoundaryStates)
        {
            BufferRange range = GraphStructureIndex.ResolveRange(endpoint.Range, buffer.Description.Size);
            int first = BinarySearch(coordinates, range.Offset);
            int last = BinarySearch(coordinates, checked(range.Offset + range.Size));
            if (first < 0 || last < 0) continue;
            for (int segment = first; segment < last; segment++)
                contents[segment] = endpoint.Contents;
        }
    }

    private static void InitializeTextureContents(
        in FrameTexture texture,
        Span<ResourceContentState> contents)
    {
        if (texture.EntryBoundaryStates is null) return;
        foreach (TextureBoundaryState endpoint in texture.EntryBoundaryStates)
            foreach (int cell in TextureCells(texture, endpoint.Range))
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

    private static int TextureCellCount(in FrameTexture texture)
    {
        int aspectCount = TextureAspectCount(texture.Format);
        return checked(aspectCount * (int)(texture.ArrayLayerCount * texture.MipLevelCount));
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

    private static TextureCellEnumerable TextureCells(
        in FrameTexture texture,
        in TextureSubresourceRange range) =>
        new(texture.MipLevelCount, texture.ArrayLayerCount, texture.Format, range);

    private readonly struct TextureCellEnumerable
    {
        private readonly uint _mipCount;
        private readonly uint _layerCount;
        private readonly Format _format;
        private readonly TextureSubresourceRange _range;

        internal TextureCellEnumerable(
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
        if (!values.Contains(value)) values.Add(value);
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (values.Length >= count) return;
        int capacity = values.Length == 0 ? 4 : values.Length;
        while (capacity < count) capacity = checked(capacity * 2);
        values = new T[capacity];
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
}

