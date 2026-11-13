namespace SomeEngine.RenderGraph;

internal sealed partial class FrameRecording
{
    private List<int> CollectResourceAccesses(
        GraphAccessTargetKind kind,
        int resourceIndex,
        bool enabledOnly)
    {
        _resourceAccessScratch.Clear();
        if (_resourceAccessLists.TryGetValue((kind, resourceIndex), out List<int>? accesses))
            foreach (int index in accesses)
                if (!enabledOnly || _passes[_accesses[index].PassIndex].Enabled) _resourceAccessScratch.Add(index);
        return _resourceAccessScratch;
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

    // Whole entries resolve against each descriptor's own view range.
    private readonly HashSet<(BufferRangeSet Ranges, BufferRange ViewRange)> _coordinateRangeSets = [];

    private int BuildBufferCoordinates(in FrameBuffer buffer, List<int> accesses)
    {
        ReadOnlySpan<BufferBoundaryState> entryStates = BufferEntryStates(buffer);
        int endpointCount = entryStates.Length;
        _coordinateRangeSets.Clear();
        int rangeCount = 0;
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.SharedBufferRanges is null ||
                _coordinateRangeSets.Add((access.SharedBufferRanges, access.BufferRange)))
                rangeCount = checked(rangeCount + access.BufferRanges.Count);
        }
        int required = checked((rangeCount + endpointCount) * 2 + 2);
        EnsureCapacity(ref _bufferCoordinateScratch, required);
        int count = 0;
        _bufferCoordinateScratch[count++] = 0;
        _bufferCoordinateScratch[count++] = buffer.Description.Size;
        if (!entryStates.IsEmpty)
        {
            foreach (BufferBoundaryState endpoint in entryStates)
            {
                BufferRange range = GraphStructureIndex.ResolveRange(
                    endpoint.Range,
                    buffer.Description.Size,
                    buffer.Description.Label);
                _bufferCoordinateScratch[count++] = range.Offset;
                _bufferCoordinateScratch[count++] = checked(range.Offset + range.Size);
            }
        }
        _coordinateRangeSets.Clear();
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.SharedBufferRanges is not null &&
                !_coordinateRangeSets.Add((access.SharedBufferRanges, access.BufferRange))) continue;
            foreach (BufferRange range in access.BufferRanges)
            {
                _bufferCoordinateScratch[count++] = range.Offset;
                _bufferCoordinateScratch[count++] = checked(range.Offset + range.Size);
            }
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
        Span<ResourceContentState> contents,
        Span<BufferContentScope> contentScopes)
    {
        foreach (BufferBoundaryState endpoint in BufferEntryStates(buffer))
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

    private void PrepareSharedWriterScratch(int count)
    {
        if (_sharedWriterScratch.Length < count)
            EnsureCapacity(ref _sharedWriterScratch, count);
        for (int index = 0; index < count; index++)
            (_sharedWriterScratch[index] ??= []).Clear();
    }

    private static bool HasRequiredBufferContents(
        ResourceContentState contents,
        BufferContentScope availableScope,
        BufferContentScope requiredScope) =>
        contents == ResourceContentState.Defined &&
        (availableScope == BufferContentScope.FullRange ||
         availableScope == requiredScope ||
         requiredScope == BufferContentScope.CountBoundedRanges);

    private void InitializeTextureContents(
        in FrameTexture texture,
        Span<ResourceContentState> contents)
    {
        foreach (TextureBoundaryState endpoint in TextureEntryStates(texture))
            foreach (int cell in TextureRegions(texture, endpoint.Range))
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

    private TextureRegionEnumerable TextureRegions(
        in FrameTexture texture,
        in TextureSubresourceRange range)
    {
        uint firstMip = checked((uint)BinarySearch(_textureMipCoordinates.AsSpan(0, _textureMipCoordinateCount), range.FirstMipLevel));
        uint mipEnd = checked((uint)BinarySearch(_textureMipCoordinates.AsSpan(0, _textureMipCoordinateCount), range.FirstMipLevel + range.MipLevelCount));
        uint firstLayer = checked((uint)BinarySearch(_textureLayerCoordinates.AsSpan(0, _textureLayerCoordinateCount), range.FirstArrayLayer));
        uint layerEnd = checked((uint)BinarySearch(_textureLayerCoordinates.AsSpan(0, _textureLayerCoordinateCount), range.FirstArrayLayer + range.ArrayLayerCount));
        return new TextureRegionEnumerable((uint)(_textureMipCoordinateCount - 1), (uint)(_textureLayerCoordinateCount - 1),
            texture.Format, new TextureSubresourceRange(firstMip, mipEnd - firstMip, firstLayer, layerEnd - firstLayer, range.Aspects));
    }

    internal readonly struct TextureRegionEnumerable
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


    private void ObserveBuffer(int index, ref FrameBuffer buffer)
    {
        FrameBuffer previous = _buffers[index];
        bool changed = previous.Description.Size != buffer.Description.Size || previous.Source != buffer.Source ||
            previous.Lifetime != buffer.Lifetime;
        ReadOnlySpan<BufferBoundaryState> states = buffer.Lifetime == RenderGraphResourceLifetime.Persistent
            ? buffer.Definition?.BoundaryStates ?? buffer.EntryBoundaryStates
            : buffer.Source == RenderGraphResourceSource.GraphCreated
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
        if (changed) InvalidateResourceState(GraphAccessTargetKind.Buffer, index);
    }

    private void ObserveTexture(int index, ref FrameTexture texture)
    {
        FrameTexture previous = _textures[index];
        bool changed = previous.Width != texture.Width || previous.Height != texture.Height || previous.Depth != texture.Depth ||
            previous.MipLevelCount != texture.MipLevelCount || previous.ArrayLayerCount != texture.ArrayLayerCount || previous.SampleCount != texture.SampleCount ||
            previous.Format != texture.Format || previous.Source != texture.Source || previous.Lifetime != texture.Lifetime;
        ReadOnlySpan<TextureBoundaryState> states = texture.Lifetime == RenderGraphResourceLifetime.Persistent
            ? texture.Definition?.BoundaryStates ?? texture.EntryBoundaryStates
            : texture.Source == RenderGraphResourceSource.GraphCreated ? default : texture.EntryBoundaryStates;
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
        if (changed) InvalidateResourceState(GraphAccessTargetKind.Texture, index);
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
        if (previous.Connected) InvalidateResourceState(previous.TargetKind, previous.ResourceIndex);
        InvalidateResourceState(current.TargetKind, current.ResourceIndex);
    }

    private void InvalidateResourceState(GraphAccessTargetKind kind, int index)
    {
        _stateUpdateResourcesChanged.Add((kind, index));
        ChangeSynchronization((kind, index));
    }

    private bool ResourceExists((GraphAccessTargetKind Kind, int Index) resource) => resource.Index >= 0 && resource.Index < (resource.Kind switch
    {
        GraphAccessTargetKind.Buffer => _buffers.Count,
        GraphAccessTargetKind.Texture => _textures.Count,
        GraphAccessTargetKind.QueryPool => _queryPools.Count,
        GraphAccessTargetKind.RayTracingShaderTable => _shaderTables.Count,
        _ => 0,
    });

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

    private uint[] _textureMipCoordinates = [];
    private uint[] _textureLayerCoordinates = [];
    private int _textureMipCoordinateCount;
    private int _textureLayerCoordinateCount;

    private int BuildTextureCoordinates(in FrameTexture texture, List<int> accesses)
    {
        ReadOnlySpan<TextureBoundaryState> entryStates = TextureEntryStates(texture);
        int required = checked((accesses.Count + entryStates.Length) * 2 + 2);
        EnsureCapacity(ref _textureMipCoordinates, required);
        EnsureCapacity(ref _textureLayerCoordinates, required);
        _textureMipCoordinates[0] = _textureLayerCoordinates[0] = 0;
        _textureMipCoordinates[1] = texture.MipLevelCount;
        _textureLayerCoordinates[1] = texture.ArrayLayerCount;
        int count = 2;
        foreach (TextureBoundaryState endpoint in entryStates) Add(endpoint.Range);
        foreach (int index in accesses) Add(_accesses[index].TextureRange);
        _textureMipCoordinateCount = Unique(_textureMipCoordinates, count);
        _textureLayerCoordinateCount = Unique(_textureLayerCoordinates, count);
        return checked((_textureMipCoordinateCount - 1) * (_textureLayerCoordinateCount - 1) * TextureAspectCount(texture.Format));

        void Add(in TextureSubresourceRange range)
        {
            _textureMipCoordinates[count] = range.FirstMipLevel;
            _textureLayerCoordinates[count++] = range.FirstArrayLayer;
            _textureMipCoordinates[count] = checked(range.FirstMipLevel + range.MipLevelCount);
            _textureLayerCoordinates[count++] = checked(range.FirstArrayLayer + range.ArrayLayerCount);
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
