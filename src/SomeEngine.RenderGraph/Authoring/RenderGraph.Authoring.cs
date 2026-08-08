using System.Reflection;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private ArenaColumn<ResourceUnversionedData> _buffers;
    private ArenaColumn<ResourceUnversionedData> _textures;
    private ReferenceColumn<BufferDesc> _bufferDescriptions = new(128);
    private ReferenceColumn<GraphTextureDescription> _textureDescriptions = new(64);
    private ReferenceColumn<Resource> _importedResources = new(64);
    private ReferenceColumn<SwapchainImage> _swapchainImages = new(0);
    private ReferenceColumn<QueueCompletion> _importReadinessRows = new(0);
    private bool _hasImportReadiness;
    private ReferenceColumn<ReadOnlyMemory<byte>> _bufferInitialData = new(0);
    private ArenaColumn<int> _bufferViewResources;
    private ArenaColumn<BufferRange> _bufferViewRanges;
    private ArenaColumn<GraphBindingType> _bufferViewTypes;
    private ArenaColumn<Format> _bufferViewFormats;
    private ArenaColumn<uint> _bufferViewStrides;
    private ArenaColumn<int> _textureViewResources;
    private ArenaColumn<TextureSubresourceRange> _textureViewRanges;
    private ArenaColumn<GraphTextureViewUsage> _textureViewUsages;
    private ArenaColumn<Format> _textureViewFormats;
    private ArenaColumn<TextureViewDimension> _textureViewDimensions;
    private ArenaSlice<int> _sharedBufferViewBuckets;
    private ArenaSlice<int> _sharedTextureViewBuckets;
    private int _sharedBufferViewCount;
    private int _sharedTextureViewCount;
    private ArenaColumn<int> _accelerationStructureBuffers;
    private ArenaColumn<BufferRange> _accelerationStructureRanges;
    private ArenaColumn<AccelerationStructureType> _accelerationStructureTypes;
    private ReferenceColumn<string?> _bufferViewNames = new(0);
    private ReferenceColumn<string?> _textureViewNames = new(0);
    private ArenaColumn<PassData> _passes;
    private ReferenceColumn<string> _passNames = new(64);
    private ReferenceColumn<PassExecutor?> _passExecutors = new(64);
    private ArenaColumn<PassInputData> _accesses;
    private ArenaColumn<int> _accessPredecessors;
    private ArenaColumn<PassAccessHead> _bufferAccessHeads;
    private ArenaColumn<PassAccessHead> _textureAccessHeads;
    private ArenaColumn<PassFragmentData> _colorAttachments;
    private ArenaColumn<PassFragmentData> _depthStencilAttachments;
    private ArenaColumn<uint> _shaderArgumentGroups;
    private ArenaColumn<uint> _shaderArgumentBindings;
    private ArenaColumn<uint> _shaderArgumentElements;
    private ArenaColumn<GraphBindingType> _shaderArgumentTypes;
    private ArenaColumn<int> _shaderArgumentAccesses;
    private ArenaColumn<int> _shaderArgumentViews;
    private ArenaColumn<int> _shaderArgumentSamplers;
    private ArenaColumn<int> _passQueries;
    private ArenaColumn<int> _bindlessAccessTables;
    private ArenaColumn<GraphBindingType> _bindlessAccessTypes;
    private ArenaColumn<int> _bindlessAccesses;
    private ArenaColumn<int> _bindlessAccessViews;
    private ReferenceColumn<Sampler> _samplers = new(0);
    private ReferenceColumn<DescriptorTable> _descriptorTables = new(0);
    private ReferenceColumn<QueryPool> _queryPools = new(0);
    private readonly GraphArena _arena = new();
    private ReferenceColumn<Pipeline?> _passPipelines = new(0);
    private ReferenceColumn<VariableLayoutReflection> _parameterLayouts = new(0);
    private ReferenceColumn<byte[]> _parameterOrdinaryData = new(0);
    private int _openPass = -1;
    private int _declarationPass = -1;
    private int _declarationAccessCursor;
    private int _declarationAccessEnd;
    private int _declarationColorCursor;
    private int _declarationColorEnd;
    private int _declarationDepthStencilStart;
    private int _declarationDepthStencilCursor;
    private int _declarationDepthStencilEnd;
    private int _declarationShaderArgumentCursor;
    private int _declarationShaderArgumentEnd;
    private int _declarationQueryCursor;
    private int _declarationQueryEnd;
    private int _declarationBindlessCursor;
    private int _declarationBindlessEnd;
    private bool _dynamicDeclarations;
    private bool _consumed;

    internal long GraphSerial { get; private set; }
    internal BufferHandle AddBuffer(in BufferDesc desc, MemoryType memoryType = MemoryType.DeviceLocal)
    {
        EnsureAuthoring();
        int ordinal = _buffers.Count;
        _bufferDescriptions.Add(desc);
        _buffers.Add(new ResourceUnversionedData(memoryType));
        _bufferAccessHeads.Add(default);
        return new BufferHandle(GraphSerial, ordinal);
    }

    internal BufferHandle AddBufferImport(
        Buffer handle,
        GraphResourceUsage initialState,
        GraphResourceUsage finalState,
        bool contentsAvailable,
        ReadOnlySpan<QueueCompletion> readiness)
    {
        EnsureAuthoring();
        int ordinal = _buffers.Count;
        for (int index = 0; index < _importedResources.Count; index++)
            if (_importedResources[index] == handle)
                throw new InvalidOperationException("A physical buffer may be imported only once per graph invocation.");
        ValidateImport(handle);
        int importOrdinal = _importedResources.Count;
        (int readinessOffset, int readinessCount) = AddImportReadiness(readiness);
        _importedResources.Add(handle);
        _bufferDescriptions.Add(new BufferDesc(
            handle.Info.Size,
            handle.Info.Usages,
            handle.Label));
        _buffers.Add(new ResourceUnversionedData(
            handle.Info.MemoryType,
            importOrdinal,
            initialState,
            finalState,
            contentsAvailable,
            readinessOffset,
            readinessCount));
        _bufferAccessHeads.Add(default);
        return new BufferHandle(GraphSerial, ordinal);
    }

    internal void SetUploadData(
        BufferHandle buffer,
        ReadOnlySpan<byte> initialData)
    {
        EnsureAuthoring();
        if (initialData.IsEmpty)
            throw new ArgumentException(
                "Upload initialization data cannot be empty.",
                nameof(initialData));
        int resource = ResolveBuffer(buffer);
        ref ResourceUnversionedData row = ref _buffers[resource];
        if (row.IsImported)
            throw new ArgumentException(
                "Imported upload buffers must be initialized by their owner before import.",
                nameof(buffer));
        if (row.MemoryType != MemoryType.Upload)
            throw new ArgumentException(
                "Only an upload-memory buffer can carry host initialization data.",
                nameof(buffer));
        if (row.ContentsInitialized)
            throw new InvalidOperationException(
                "Upload initialization data can be assigned only once.");
        ulong size = _bufferDescriptions[resource].Size;
        if (size > int.MaxValue ||
            initialData.Length != checked((int)size))
        {
            throw new ArgumentException(
                "Upload initialization must cover the complete buffer and fit in managed memory.",
                nameof(initialData));
        }

        long address = _arena.AllocateBytes(
            initialData.Length,
            alignment: 1,
            out Span<byte> destination);
        initialData.CopyTo(destination);
        row.InitialDataAddress = address;
        row.InitialDataLength = initialData.Length;
    }

    internal TextureHandle AddTexture(in GraphTextureDescription desc)
    {
        EnsureAuthoring();
        int ordinal = _textures.Count;
        _textureDescriptions.Add(desc);
        _textures.Add(new ResourceUnversionedData(MemoryType.DeviceLocal));
        _textureAccessHeads.Add(default);
        return new TextureHandle(GraphSerial, ordinal);
    }

    internal TextureHandle AddTextureImport(
        Texture handle,
        GraphResourceUsage initialState,
        GraphResourceUsage finalState,
        bool contentsAvailable,
        ReadOnlySpan<QueueCompletion> readiness,
        SwapchainImage? swapchainImage = null)
    {
        EnsureAuthoring();
        int ordinal = _textures.Count;
        for (int index = 0; index < _importedResources.Count; index++)
            if (_importedResources[index] == handle)
                throw new InvalidOperationException("A physical texture may be imported only once per graph invocation.");
        ValidateImport(handle);
        int importOrdinal = _importedResources.Count;
        int swapchainImageOrdinal = -1;
        (int readinessOffset, int readinessCount) = AddImportReadiness(readiness);
        _importedResources.Add(handle);
        if (swapchainImage is SwapchainImage image)
        {
            swapchainImageOrdinal = _swapchainImages.Count;
            _swapchainImages.Add(image);
        }
        _textureDescriptions.Add(new GraphTextureDescription(handle.Info, handle.Label));
        _textures.Add(new ResourceUnversionedData(
            handle.Info.MemoryType,
            importOrdinal,
            initialState,
            finalState,
            contentsAvailable,
            readinessOffset,
            readinessCount,
            swapchainImageOrdinal));
        _textureAccessHeads.Add(default);
        return new TextureHandle(GraphSerial, ordinal);
    }

    private (int Offset, int Count) AddImportReadiness(
        ReadOnlySpan<QueueCompletion> readiness)
    {
        if (readiness.IsEmpty) return (0, 0);
        QueueCompletion[] merged = new QueueCompletion[3];
        int mergedCount = 0;
        foreach (ref readonly QueueCompletion fence in readiness)
        {
            if (fence == default || !ReferenceEquals(fence.Queue.Device, _device))
                throw new ArgumentException("Imported-resource readiness is invalid or belongs to another device.", nameof(readiness));
            int existing = -1;
            for (int index = 0; index < mergedCount; index++)
            {
                if (ReferenceEquals(merged[index].Queue, fence.Queue))
                {
                    existing = index;
                    break;
                }
            }
            if (existing >= 0)
            {
                if (fence.Value > merged[existing].Value)
                    merged[existing] = fence;
            }
            else
            {
                if (mergedCount == merged.Length)
                    throw new ArgumentException("Readiness contains more queues than the device exposes.", nameof(readiness));
                merged[mergedCount++] = fence;
            }
        }
        _hasImportReadiness = true;
        int offset = _importReadinessRows.Count;
        for (int index = 0; index < mergedCount; index++)
            _importReadinessRows.Add(merged[index]);
        return (offset, mergedCount);
    }

    private void ValidateImport(Resource handle)
    {
        if (!ReferenceEquals(handle.Device, _device))
            throw new ArgumentException("A cross-device imported resource belongs to another device.");

        foreach (Resource prior in _importedResources.ReadOnlySpan)
            ValidateImportDoesNotOverlap(prior, handle);
    }

    private static void ValidateImportDoesNotOverlap(
        Resource prior,
        Resource current)
    {
        (object priorMemory, ulong priorOffset, ulong priorEnd) = MemoryRange(prior);
        (object currentMemory, ulong currentOffset, ulong currentEnd) = MemoryRange(current);
        if (ReferenceEquals(priorMemory, currentMemory) &&
            currentOffset < priorEnd && priorOffset < currentEnd)
        {
            throw new InvalidOperationException(
                "Two imported resources overlap in one physical allocation.");
        }
    }

    private static (object Memory, ulong Offset, ulong End) MemoryRange(Resource resource) =>
        resource switch
        {
            Buffer buffer => (
                (object?)buffer.Heap ?? buffer,
                buffer.Info.AllocationOffset,
                checked(buffer.Info.AllocationOffset + buffer.Info.AllocationSize)),
            Texture texture => (
                (object?)texture.Heap ?? texture,
                texture.Info.AllocationOffset,
                checked(texture.Info.AllocationOffset + texture.Info.AllocationSize)),
            _ => throw new ArgumentOutOfRangeException(nameof(resource)),
        };

    internal bool HasImportReadiness => _hasImportReadiness;

    internal BufferViewHandle AddBufferView(
        BufferHandle buffer,
        BufferRange? range,
        GraphBindingType kind,
        Format? format,
        uint stride,
        string? name) =>
        AddBufferViewCore(
            buffer,
            range,
            kind,
            format,
            stride,
            name,
            shared: false);

    internal BufferViewHandle AddSharedBufferView(
        BufferHandle buffer,
        BufferRange? range,
        GraphBindingType kind,
        Format? format,
        uint stride,
        string? name) =>
        AddBufferViewCore(
            buffer,
            range,
            kind,
            format,
            stride,
            name,
            shared: true);

    private BufferViewHandle AddBufferViewCore(
        BufferHandle buffer,
        BufferRange? range,
        GraphBindingType kind,
        Format? format,
        uint stride,
        string? name,
        bool shared)
    {
        EnsureAuthoring();
        if (kind == GraphBindingType.AccelerationStructure)
            throw new ArgumentException(
                "Acceleration structures must be declared with CreateAccelerationStructure.",
                nameof(kind));
        int resource = ResolveBuffer(buffer);
        ref readonly BufferDesc bufferDesc = ref GetBufferDescription(resource);
        BufferRange normalized = AccessNormalizer.NormalizeBuffer(bufferDesc.Size, range);
        ValidateBufferView(bufferDesc.Usages, kind, format, stride, normalized);
        if (shared)
        {
            int existing = FindSharedBufferView(
                resource,
                normalized,
                kind,
                format,
                stride);
            if (existing >= 0)
                return new BufferViewHandle(GraphSerial, existing);
        }
        int ordinal = _bufferViewResources.Count;
        _bufferViewResources.Add(resource);
        _bufferViewRanges.Add(normalized);
        _bufferViewTypes.Add(kind);
        _bufferViewFormats.Add(format ?? default);
        _bufferViewStrides.Add(stride);
        AppendOptionalName(ref _bufferViewNames, ordinal, name);
        if (shared)
            RegisterSharedBufferView(ordinal);
        return new BufferViewHandle(GraphSerial, ordinal);
    }

    internal AccelerationStructureHandle AddAccelerationStructure(
        BufferHandle storage,
        BufferRange range,
        AccelerationStructureType type,
        string? name)
    {
        EnsureAuthoring();
        if (type != AccelerationStructureType.TopLevel)
            throw new ArgumentException(
                "Shader-visible acceleration structures must be top-level acceleration structures.",
                nameof(type));
        if (range.Size is 0 or ulong.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(range),
                "A graph acceleration structure requires an exact, non-empty byte range.");
        if ((range.Offset & 255) != 0)
            throw new ArgumentException(
                "Acceleration-structure storage must be 256-byte aligned.",
                nameof(range));
        int resource = ResolveBuffer(storage);
        ref readonly BufferDesc bufferDesc = ref GetBufferDescription(resource);
        if ((bufferDesc.Usages & BufferUsages.AccelerationStructure) == 0)
            throw new ArgumentException(
                "Acceleration-structure storage requires AccelerationStructure buffer usage.",
                nameof(storage));
        BufferRange normalized = AccessNormalizer.NormalizeBuffer(bufferDesc.Size, range);
        int ordinal = _accelerationStructureBuffers.Count;
        _accelerationStructureBuffers.Add(resource);
        _accelerationStructureRanges.Add(normalized);
        _accelerationStructureTypes.Add(type);
        return new AccelerationStructureHandle(GraphSerial, ordinal);
    }

    internal TextureViewHandle AddTextureView(
        TextureHandle texture,
        TextureSubresourceRange? range,
        GraphTextureViewUsage usage,
        Format? format,
        string? name,
        TextureViewDimension? dimension = null) =>
        AddTextureViewCore(
            texture,
            range,
            usage,
            format,
            name,
            dimension,
            shared: false);

    internal TextureViewHandle AddSharedTextureView(
        TextureHandle texture,
        TextureSubresourceRange? range,
        GraphTextureViewUsage usage,
        Format? format,
        string? name,
        TextureViewDimension? dimension = null) =>
        AddTextureViewCore(
            texture,
            range,
            usage,
            format,
            name,
            dimension,
            shared: true);

    private TextureViewHandle AddTextureViewCore(
        TextureHandle texture,
        TextureSubresourceRange? range,
        GraphTextureViewUsage usage,
        Format? format,
        string? name,
        TextureViewDimension? dimension,
        bool shared)
    {
        EnsureAuthoring();
        int resource = ResolveTexture(texture);
        GraphTextureDescription description = GetTextureDescription(resource);
        TextureViewDimension resolvedDimension = dimension ?? InferTextureViewDimension(description);
        GraphTextureViewValidation.Normalize(
            description,
            range,
            usage,
            format,
            resolvedDimension,
            out TextureSubresourceRange normalizedRange,
            out Format normalizedFormat);
        if (shared)
        {
            int existing = FindSharedTextureView(
                resource,
                normalizedRange,
                usage,
                normalizedFormat,
                resolvedDimension);
            if (existing >= 0)
                return new TextureViewHandle(GraphSerial, existing);
        }
        int ordinal = _textureViewResources.Count;
        _textureViewResources.Add(resource);
        _textureViewRanges.Add(normalizedRange);
        _textureViewUsages.Add(usage);
        _textureViewFormats.Add(normalizedFormat);
        _textureViewDimensions.Add(resolvedDimension);
        AppendOptionalName(ref _textureViewNames, ordinal, name);
        if (shared)
            RegisterSharedTextureView(ordinal);
        return new TextureViewHandle(GraphSerial, ordinal);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSharedBufferView(
        int resource,
        in BufferRange range,
        GraphBindingType type,
        Format? format,
        uint stride)
    {
        if (_sharedBufferViewBuckets.IsEmpty)
            return -1;
        int mask = _sharedBufferViewBuckets.Length - 1;
        int slot = GetBufferViewHash(resource, range, type, format, stride) & mask;
        while (_sharedBufferViewBuckets[slot] != 0)
        {
            int ordinal = _sharedBufferViewBuckets[slot] - 1;
            if (BufferViewEquals(ordinal, resource, range, type, format, stride))
                return ordinal;
            slot = (slot + 1) & mask;
        }
        return -1;
    }

    private static int GetBufferViewHash(
        int resource,
        in BufferRange range,
        GraphBindingType type,
        Format? format,
        uint stride) => HashCode.Combine(resource, range, type, format, stride);

    private bool BufferViewEquals(
        int ordinal,
        int resource,
        in BufferRange range,
        GraphBindingType type,
        Format? format,
        uint stride) =>
        _bufferViewResources[ordinal] == resource &&
        _bufferViewRanges[ordinal] == range &&
        _bufferViewTypes[ordinal] == type &&
        _bufferViewFormats[ordinal] == (format ?? default) &&
        _bufferViewStrides[ordinal] == stride;

    private void RegisterSharedBufferView(int ordinal)
    {
        if (_sharedBufferViewBuckets.IsEmpty ||
            checked((_sharedBufferViewCount + 1) * 2) >
            _sharedBufferViewBuckets.Length)
        {
            GrowSharedBufferViewBuckets();
        }
        int mask = _sharedBufferViewBuckets.Length - 1;
        int slot = GetBufferViewHash(ordinal) & mask;
        while (_sharedBufferViewBuckets[slot] != 0)
            slot = (slot + 1) & mask;
        _sharedBufferViewBuckets[slot] = checked(ordinal + 1);
        _sharedBufferViewCount++;
    }

    private void GrowSharedBufferViewBuckets()
    {
        int capacity = _sharedBufferViewBuckets.IsEmpty
            ? 256
            : checked(_sharedBufferViewBuckets.Length * 2);
        ArenaSlice<int> replacement = AllocateSlice<int>(capacity);
        int mask = capacity - 1;
        foreach (int encoded in _sharedBufferViewBuckets)
        {
            if (encoded == 0)
                continue;
            int ordinal = encoded - 1;
            int slot = GetBufferViewHash(ordinal) & mask;
            while (replacement[slot] != 0)
                slot = (slot + 1) & mask;
            replacement[slot] = encoded;
        }
        _sharedBufferViewBuckets = replacement;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindSharedTextureView(
        int resource,
        in TextureSubresourceRange range,
        GraphTextureViewUsage usage,
        Format format,
        TextureViewDimension dimension)
    {
        if (_sharedTextureViewBuckets.IsEmpty)
            return -1;
        int mask = _sharedTextureViewBuckets.Length - 1;
        int slot = GetTextureViewHash(resource, range, usage, format, dimension) & mask;
        while (_sharedTextureViewBuckets[slot] != 0)
        {
            int ordinal = _sharedTextureViewBuckets[slot] - 1;
            if (TextureViewEquals(ordinal, resource, range, usage, format, dimension))
                return ordinal;
            slot = (slot + 1) & mask;
        }
        return -1;
    }

    private static int GetTextureViewHash(
        int resource,
        in TextureSubresourceRange range,
        GraphTextureViewUsage usage,
        Format format,
        TextureViewDimension dimension) =>
        HashCode.Combine(resource, range, usage, format, dimension);

    private bool TextureViewEquals(
        int ordinal,
        int resource,
        in TextureSubresourceRange range,
        GraphTextureViewUsage usage,
        Format format,
        TextureViewDimension dimension) =>
        _textureViewResources[ordinal] == resource &&
        _textureViewRanges[ordinal] == range &&
        _textureViewUsages[ordinal] == usage &&
        _textureViewFormats[ordinal] == format &&
        _textureViewDimensions[ordinal] == dimension;

    private void RegisterSharedTextureView(int ordinal)
    {
        if (_sharedTextureViewBuckets.IsEmpty ||
            checked((_sharedTextureViewCount + 1) * 2) >
            _sharedTextureViewBuckets.Length)
        {
            GrowSharedTextureViewBuckets();
        }
        int mask = _sharedTextureViewBuckets.Length - 1;
        int slot = GetTextureViewHash(ordinal) & mask;
        while (_sharedTextureViewBuckets[slot] != 0)
            slot = (slot + 1) & mask;
        _sharedTextureViewBuckets[slot] = checked(ordinal + 1);
        _sharedTextureViewCount++;
    }

    private void GrowSharedTextureViewBuckets()
    {
        int capacity = _sharedTextureViewBuckets.IsEmpty
            ? 128
            : checked(_sharedTextureViewBuckets.Length * 2);
        ArenaSlice<int> replacement = AllocateSlice<int>(capacity);
        int mask = capacity - 1;
        foreach (int encoded in _sharedTextureViewBuckets)
        {
            if (encoded == 0)
                continue;
            int ordinal = encoded - 1;
            int slot = GetTextureViewHash(ordinal) & mask;
            while (replacement[slot] != 0)
                slot = (slot + 1) & mask;
            replacement[slot] = encoded;
        }
        _sharedTextureViewBuckets = replacement;
    }

    private SamplerHandle AddSamplerImport(Sampler sampler)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(sampler);
        if (!ReferenceEquals(sampler.Device, _device))
            throw new ArgumentException("The sampler belongs to another device.", nameof(sampler));
        int ordinal = _samplers.Count;
        _samplers.Add(sampler);
        return new SamplerHandle(GraphSerial, ordinal);
    }

    private DescriptorTableHandle AddDescriptorTableImport(DescriptorTable table)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(table);
        if (!ReferenceEquals(table.Device, _device))
            throw new ArgumentException("The bindless table belongs to another device.", nameof(table));
        int ordinal = _descriptorTables.Count;
        _descriptorTables.Add(table);
        return new DescriptorTableHandle(GraphSerial, ordinal);
    }

    private QueryPoolHandle AddQueryPoolImport(QueryPool pool)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(pool);
        if (!ReferenceEquals(pool.Device, _device))
            throw new ArgumentException("The query pool belongs to another device.", nameof(pool));
        int ordinal = _queryPools.Count;
        _queryPools.Add(pool);
        return new QueryPoolHandle(GraphSerial, ordinal);
    }

    private Sampler GetSampler(SamplerHandle sampler)
    {
        if (sampler.Graph != GraphSerial ||
            (uint)sampler.Ordinal >= (uint)_samplers.Count)
        {
            throw new ArgumentException(
                "The sampler belongs to another graph invocation or has the wrong kind.",
                nameof(sampler));
        }
        return GetSampler(sampler.Ordinal);
    }

    internal Sampler GetSampler(int ordinal)
    {
        if ((uint)ordinal >= (uint)_samplers.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _samplers[ordinal];
    }

    internal DescriptorTable GetDescriptorTable(DescriptorTableHandle table)
    {
        if (table.Graph != GraphSerial ||
            (uint)table.Ordinal >= (uint)_descriptorTables.Count)
        {
            throw new ArgumentException(
                "The bindless table belongs to another graph invocation or has the wrong kind.",
                nameof(table));
        }
        return GetDescriptorTable(table.Ordinal);
    }

    internal DescriptorTable GetDescriptorTable(int ordinal)
    {
        if ((uint)ordinal >= (uint)_descriptorTables.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _descriptorTables[ordinal];
    }

    internal QueryPool GetQueryPool(QueryPoolHandle query)
    {
        if (query.Graph != GraphSerial ||
            (uint)query.Ordinal >= (uint)_queryPools.Count)
        {
            throw new ArgumentException(
                "The query pool belongs to another graph invocation or has the wrong kind.",
                nameof(query));
        }
        return _queryPools[query.Ordinal];
    }

    internal QueryPool GetQueryPool(int ordinal)
    {
        if ((uint)ordinal >= (uint)_queryPools.Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _queryPools[ordinal];
    }

    private static TextureViewDimension InferTextureViewDimension(in GraphTextureDescription texture) => texture.Dimension switch
    {
        TextureDimension.Texture1D => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture1DArray
            : TextureViewDimension.Texture1D,
        TextureDimension.Texture2D when texture.SampleCount > 1 => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture2DMultisampledArray
            : TextureViewDimension.Texture2DMultisampled,
        TextureDimension.Texture2D => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture2DArray
            : TextureViewDimension.Texture2D,
        TextureDimension.Texture3D => TextureViewDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(texture)),
    };

    private static void AppendOptionalName(
        ref ReferenceColumn<string?> names,
        int ordinal,
        string? name)
    {
        if (names.Count == 0)
        {
            if (name is null) return;
            names.EnsureCapacity(64);
            names.AddDefault(ordinal);
        }
        names.Add(name);
    }

    private int BeginPass(
        string name,
        QueueType queue,
        PassFlags flags)
    {
        if (_openPass >= 0)
            throw new InvalidOperationException("A pass is already being authored.");
        EnsureAuthoring();
        if (!Enum.IsDefined(queue))
            throw new ArgumentOutOfRangeException(nameof(queue));
        const PassFlags allFlags = PassFlags.NeverCull | PassFlags.NeverParallel | PassFlags.NeverMerge;
        if ((flags & ~allFlags) != 0)
            throw new ArgumentOutOfRangeException(nameof(flags));
        int pass = _passes.Count;
        _passes.Add(new PassData(queue, flags));
        _passNames.Add(name);
        _passExecutors.Add(null);
        if (_passPipelines.Count != 0) _passPipelines.Add(null);
        _parameterLayouts.Add(VariableLayoutReflection.Null);
        _parameterOrdinaryData.Add([]);
        _openPass = pass;
        return pass;
    }

    private void EndPass(int pass)
    {
        if (_openPass != pass)
            throw new InvalidOperationException("The active pass does not match this pass.");
        if (_declarationPass >= 0)
            throw new InvalidOperationException("The generated pass declaration was not completed.");
        ref PassData row = ref GetPass(pass);
        if (_passExecutors[pass] is null)
            throw new InvalidOperationException($"Pass '{GetPassName(pass)}' has no command encoding operation.");
        if (row.ColorAttachmentCount > 1)
        {
            GetPassColorAttachmentRows(row).Sort(
                static (left, right) => left.Slot.CompareTo(right.Slot));
        }
        _openPass = -1;
    }

    private PassRollbackMarker BeginPassRollbackMarker() => new(
        _passes.Count,
        _accesses.Count,
        _accessPredecessors.Count,
        _colorAttachments.Count,
        _depthStencilAttachments.Count,
        _shaderArgumentTypes.Count,
        _passQueries.Count,
        _bindlessAccessTypes.Count,
        _passNames.Count,
        _passPipelines.Count,
        _parameterLayouts.Count,
        _parameterOrdinaryData.Count);

    private void RollbackPass(int pass, in PassRollbackMarker transaction)
    {
        if (_openPass != pass)
            throw new InvalidOperationException("The failed pass is not the active authoring transaction.");
        Truncate(ref _passes, transaction.Passes);
        Truncate(ref _accesses, transaction.Accesses);
        Truncate(ref _accessPredecessors, transaction.AccessPredecessors);
        Truncate(ref _colorAttachments, transaction.ColorAttachments);
        Truncate(ref _depthStencilAttachments, transaction.DepthStencilAttachments);
        Truncate(ref _shaderArgumentGroups, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentBindings, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentElements, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentTypes, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentAccesses, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentViews, transaction.ShaderArguments);
        Truncate(ref _shaderArgumentSamplers, transaction.ShaderArguments);
        Truncate(ref _bindlessAccessTables, transaction.BindlessAccesses);
        Truncate(ref _bindlessAccessTypes, transaction.BindlessAccesses);
        Truncate(ref _bindlessAccesses, transaction.BindlessAccesses);
        Truncate(ref _bindlessAccessViews, transaction.BindlessAccesses);
        Truncate(ref _passQueries, transaction.Queries);
        Truncate(ref _passNames, transaction.PassNames);
        Truncate(ref _passExecutors, transaction.Passes);
        if (_passPipelines.Count != 0) Truncate(ref _passPipelines, transaction.PassPipelines);
        Truncate(ref _parameterLayouts, transaction.ParameterLayouts);
        Truncate(ref _parameterOrdinaryData, transaction.ParameterOrdinaryData);
        _declarationPass = -1;
        _dynamicDeclarations = false;
        _openPass = -1;
    }

    private static void Truncate<T>(ref ArenaColumn<T> column, int count) where T : unmanaged
    {
        if ((uint)count > (uint)column.Count) throw new ArgumentOutOfRangeException(nameof(count));
        column.RemoveRange(count, column.Count - count);
    }

    private static void Truncate<T>(ref ReferenceColumn<T> column, int count)
    {
        if ((uint)count > (uint)column.Count) throw new ArgumentOutOfRangeException(nameof(count));
        column.RemoveRange(count, column.Count - count);
    }

    internal int GetOpenPass()
    {
        EnsureAuthoring();
        return _openPass >= 0
            ? _openPass
            : throw new InvalidOperationException(
                "Declarations are valid only while a render graph builder is active.");
    }

    private void EnsurePassPipelineColumn()
    {
        if (_passPipelines.Count == 0)
            _passPipelines.AddDefault(_passes.Count);
    }

    private void DisposeReferenceColumns()
    {
        _bufferDescriptions.Dispose();
        _textureDescriptions.Dispose();
        _importedResources.Dispose();
        _swapchainImages.Dispose();
        _importReadinessRows.Dispose();
        _bufferInitialData.Dispose();
        _bufferViewNames.Dispose();
        _textureViewNames.Dispose();
        _passNames.Dispose();
        _passExecutors.Dispose();
        _samplers.Dispose();
        _descriptorTables.Dispose();
        _queryPools.Dispose();
        _passPipelines.Dispose();
        _parameterLayouts.Dispose();
        _parameterOrdinaryData.Dispose();
        BatchExternalWaitRows.Dispose();
    }

    internal Pipeline? GetPassPipeline(int pass) =>
        (uint)pass < (uint)_passPipelines.Count ? _passPipelines[pass] : null;

    internal Pipeline RequirePassPipeline(int pass) =>
        GetPassPipeline(pass) ??
        throw new InvalidOperationException("The pass does not declare a shader pipeline.");

    private void ValidatePipeline(int pass, Pipeline pipeline)
    {
        ValidatePipelineOwner(pass, pipeline);
        PassData row = GetPass(pass);
        if (row.ShaderArgumentCount != 0 &&
            _parameterLayouts[pass] == VariableLayoutReflection.Null)
            throw new InvalidOperationException(
                "A pass with transient resource bindings must declare their Slang parameter layout.");
    }

    private void ValidatePipelineOwner(int pass, Pipeline pipeline)
    {
        if (!ReferenceEquals(pipeline.Device, _device))
            throw new ArgumentException($"Pass '{GetPassName(pass)}' declares a pipeline from another device.");
    }

    internal VariableLayoutReflection GetPassParameterLayout(int pass) =>
        _parameterLayouts[pass];

    internal ReadOnlySpan<byte> GetPassParameterOrdinaryData(int pass) =>
        _parameterOrdinaryData[pass];


    private int AddBufferViewAccess(
        int pass,
        BufferViewHandle view,
        GraphAccess flags)
    {
        EnsureAuthoring();
        int ordinal = ValidateBufferView(view);
        GraphBindingType type = _bufferViewTypes[ordinal];
        GraphResourceUsage use = type switch
        {
            GraphBindingType.ConstantBuffer => GraphResourceUsage.VertexOrConstantBuffer,
            GraphBindingType.ReadOnlyBuffer => GraphResourceUsage.ShaderResource,
            GraphBindingType.StorageBuffer => GraphResourceUsage.UnorderedAccess,
            _ => throw new ArgumentException($"Buffer view type {type} is not a shader buffer view.", nameof(view)),
        };
        ValidateViewEffect(flags, type, nameof(view));
        return AddBufferAccessCore(
            pass,
            _bufferViewResources[ordinal],
            ordinal,
            flags,
            use,
            _bufferViewRanges[ordinal]);
    }

    private int AddAccelerationStructureAccess(
        int pass,
        AccelerationStructureHandle accelerationStructure)
    {
        EnsureAuthoring();
        int ordinal = ValidateAccelerationStructure(accelerationStructure);
        return AddBufferAccessCore(
            pass,
            _accelerationStructureBuffers[ordinal],
            ordinal,
            GraphAccess.Read,
            GraphResourceUsage.AccelerationStructure,
            _accelerationStructureRanges[ordinal]);
    }

    private int AddTextureViewAccess(
        int pass,
        TextureViewHandle view,
        GraphAccess flags)
    {
        EnsureAuthoring();
        int ordinal = ValidateTextureView(view);
        GraphTextureViewUsage usage = _textureViewUsages[ordinal];
        GraphBindingType kind;
        GraphResourceUsage use;
        if ((flags & GraphAccess.ReadWrite) == GraphAccess.Read &&
            (usage & GraphTextureViewUsage.ShaderResource) != 0)
        {
            kind = GraphBindingType.SampledTexture;
            use = GraphResourceUsage.ShaderResource;
        }
        else if ((usage & GraphTextureViewUsage.Storage) != 0)
        {
            kind = GraphBindingType.StorageTexture;
            use = GraphResourceUsage.UnorderedAccess;
        }
        else
        {
            throw new ArgumentException("Shader texture accesses require a view with ShaderResource or Storage usage.", nameof(view));
        }
        ValidateViewEffect(flags, kind, nameof(view));
        return AddTextureAccessCore(
            pass,
            _textureViewResources[ordinal],
            ordinal,
            flags,
            use,
            _textureViewRanges[ordinal]);
    }

    private int AddBindlessAccess(
        int pass,
        DescriptorTableHandle table,
        GraphBindingType type,
        int accessOrdinal,
        int view)
    {
        EnsureAuthoring();
        DescriptorTable tableOwner = GetDescriptorTable(table);
        ref PassData passRow = ref GetPass(pass);
        ValidateViewAccess(pass, accessOrdinal, type, view);
        if (tableOwner.Type != DescriptorTableType.Resource)
            throw new InvalidOperationException("Shader resource views require a resource descriptor table.");
        MarkViewMaterialization(ref passRow, accessOrdinal);
        int ordinal = passRow.BindlessAccessCount;
        if (_declarationPass != pass)
            throw new InvalidOperationException("No generated declaration range is active for this pass.");
        int rowOrdinal = _declarationBindlessCursor++;
        if (rowOrdinal >= _declarationBindlessEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more bindless accesses than it reserved.");
        if (rowOrdinal >= _declarationBindlessEnd)
        {
            _ = _bindlessAccessTables.AddUninitialized(1);
            _ = _bindlessAccessTypes.AddUninitialized(1);
            _ = _bindlessAccesses.AddUninitialized(1);
            _ = _bindlessAccessViews.AddUninitialized(1);
            _declarationBindlessEnd++;
        }
        _bindlessAccessTables[rowOrdinal] = table.Ordinal;
        _bindlessAccessTypes[rowOrdinal] = type;
        _bindlessAccesses[rowOrdinal] = accessOrdinal;
        _bindlessAccessViews[rowOrdinal] = view;
        passRow.BindlessAccessCount++;
        return ordinal;
    }

    private void AddColorAttachment(
        int pass,
        int slot,
        TextureViewHandle view,
        GraphAccess flags,
        LoadType load,
        Vector4 clearColor,
        TextureViewHandle? resolveView = null,
        ResolveType resolveMode = ResolveType.Average)
    {
        EnsureAuthoring();
        if ((uint)slot >= 8u) throw new ArgumentOutOfRangeException(nameof(slot), "Color attachment slots are in the range [0, 7].");
        if (!Enum.IsDefined(load)) throw new ArgumentOutOfRangeException(nameof(load));
        int viewOrdinal = ValidateTextureView(view);
        if ((_textureViewUsages[viewOrdinal] & GraphTextureViewUsage.ColorAttachment) == 0)
            throw new ArgumentException("A color attachment requires a texture view with GraphAttachmentPlan usage.", nameof(view));
        ref PassData passRow = ref GetPass(pass);
        ReadOnlySpan<PassFragmentData> existingColors = GetPassColorAttachments(passRow);
        if (existingColors.Length != 0)
        {
            foreach (PassFragmentData attachment in existingColors)
            {
                if (attachment.Slot == slot)
                    throw new InvalidOperationException($"Pass '{GetPassName(pass)}' already declares color attachment slot {slot}.");
            }
        }

        GraphAccess effect = flags & GraphAccess.ReadWrite;
        if (load == LoadType.Load)
        {
            if ((flags & GraphAccess.Discard) != 0)
                throw new ArgumentException("A loaded color attachment cannot discard its prior contents.", nameof(flags));
        }
        else
        {
            if (effect != GraphAccess.Write)
                throw new ArgumentException("A cleared or discarded color attachment requires write-only access.", nameof(flags));
            flags |= GraphAccess.Discard;
        }
        int access = AddTextureAccessCore(
            pass,
            _textureViewResources[viewOrdinal],
            viewOrdinal,
            flags,
            GraphResourceUsage.RenderTarget,
            _textureViewRanges[viewOrdinal]);
        MarkViewMaterialization(ref passRow, access);
        int resolveViewOrdinal = -1;
        int resolveAccessOrdinal = -1;
        if (resolveView is TextureViewHandle requestedResolve)
        {
            if (!Enum.IsDefined(resolveMode)) throw new ArgumentOutOfRangeException(nameof(resolveMode));
            int destination = ValidateTextureView(requestedResolve);
            if ((_textureViewUsages[destination] & GraphTextureViewUsage.ResolveDestination) == 0)
                throw new ArgumentException(
                    "An integrated resolve destination requires a view with ResolveDestination usage.",
                    nameof(resolveView));
            resolveAccessOrdinal = AddTextureAccessCore(
                pass,
                _textureViewResources[destination],
                destination,
                GraphAccess.WriteAll,
                GraphResourceUsage.ResolveDestination,
                _textureViewRanges[destination]);
            resolveViewOrdinal = destination;
            MarkViewMaterialization(ref passRow, resolveAccessOrdinal);
        }
        if (_declarationPass != pass)
            throw new InvalidOperationException("No generated declaration range is active for this pass.");
        int colorOrdinal = _declarationColorCursor++;
        if (colorOrdinal >= _declarationColorEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more color attachments than it reserved.");
        if (colorOrdinal >= _declarationColorEnd)
        {
            _ = _colorAttachments.AddUninitialized(1);
            _declarationColorEnd++;
        }
        _colorAttachments[colorOrdinal] = new PassFragmentData(
            slot,
            viewOrdinal,
            access,
            load,
            clearColor,
            resolveViewOrdinal,
            resolveAccessOrdinal,
            resolveMode);
        passRow.ColorAttachmentCount++;
    }

    private void AddDepthStencilAttachment(
        int pass,
        TextureViewHandle view,
        bool hasDepth,
        LoadType depthLoad,
        bool depthReadOnly,
        float clearDepth,
        bool hasStencil,
        LoadType stencilLoad,
        bool stencilReadOnly,
        byte clearStencil)
    {
        EnsureAuthoring();
        if (!hasDepth && !hasStencil)
            throw new ArgumentException("A depth-stencil attachment must select at least one plane.");
        int viewOrdinal = ValidateTextureView(view);
        GraphTextureViewUsage usage = _textureViewUsages[viewOrdinal];
        TextureSubresourceRange range = _textureViewRanges[viewOrdinal];
        int resource = _textureViewResources[viewOrdinal];
        if ((usage & GraphTextureViewUsage.DepthStencilAttachment) == 0)
            throw new ArgumentException("A depth-stencil attachment requires a view with DepthStencilAttachment usage.", nameof(view));
        ref PassData passRow = ref GetPass(pass);
        if (passRow.DepthStencilAttachmentOrdinal >= 0)
            throw new InvalidOperationException($"Pass '{GetPassName(pass)}' already declares a depth-stencil attachment.");

        int depthAccessOrdinal = -1;
        int stencilAccessOrdinal = -1;
        if (hasDepth)
        {
            ValidateDepthAttachment(depthLoad, depthReadOnly, clearDepth);
            if ((range.Aspects & TextureAspects.Depth) == 0)
                throw new ArgumentException("The attachment view does not include the depth plane.", nameof(view));
            depthAccessOrdinal = AddAttachmentPlaneAccess(
                pass,
                viewOrdinal,
                GraphTextureAspect.Depth,
                depthLoad,
                depthReadOnly);
            MarkViewMaterialization(ref passRow, depthAccessOrdinal);
        }
        if (hasStencil)
        {
            ValidateStencilAttachment(stencilLoad, stencilReadOnly);
            if ((range.Aspects & TextureAspects.Stencil) == 0)
                throw new ArgumentException("The attachment view does not include the stencil plane.", nameof(view));
            if (!GraphFormat.HasStencil(
                    GetTextureDescription(resource).Format))
                throw new ArgumentException("Stencil attachment operations require a stencil-capable depth format.", nameof(view));
            stencilAccessOrdinal = AddAttachmentPlaneAccess(
                pass,
                viewOrdinal,
                GraphTextureAspect.Stencil,
                stencilLoad,
                stencilReadOnly);
            MarkViewMaterialization(ref passRow, stencilAccessOrdinal);
        }

        if (_declarationPass != pass)
            throw new InvalidOperationException("No generated declaration range is active for this pass.");
        int depthStencilOrdinal = _declarationDepthStencilCursor++;
        if (depthStencilOrdinal >= _declarationDepthStencilEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more depth-stencil attachments than it reserved.");
        if (depthStencilOrdinal >= _declarationDepthStencilEnd)
        {
            _ = _depthStencilAttachments.AddUninitialized(1);
            _declarationDepthStencilEnd++;
        }
        passRow.DepthStencilAttachmentOrdinal = depthStencilOrdinal;
        _depthStencilAttachments[depthStencilOrdinal] = new PassFragmentData(
            viewOrdinal,
            depthAccessOrdinal,
            stencilAccessOrdinal,
            hasDepth,
            depthLoad,
            depthReadOnly,
            clearDepth,
            hasStencil,
            stencilLoad,
            stencilReadOnly,
            clearStencil);
    }

    private void AddShaderArgument(
        int pass,
        uint group,
        uint binding,
        uint element,
        GraphBindingType type,
        int accessOrdinal,
        int view,
        int sampler = -1)
    {
        EnsureAuthoring();
        _ = GetPass(pass);
        if (type == GraphBindingType.Sampler)
        {
            if (accessOrdinal != -1 || view != -1 || sampler < 0)
                throw new ArgumentException("A sampler shader argument must name exactly one sampler and no graph access.");
        }
        else
        {
            if (sampler != -1)
                throw new ArgumentException("Only a sampler shader argument can name a sampler.");
            ValidateViewAccess(pass, accessOrdinal, type, view);
        }
        AppendShaderArgument(
            pass,
            group,
            binding,
            element,
            type,
            accessOrdinal,
            view,
            sampler);
        if (type != GraphBindingType.Sampler)
            MarkViewMaterialization(ref GetPass(pass), accessOrdinal);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendShaderArgument(
        int pass,
        uint group,
        uint binding,
        uint element,
        GraphBindingType type,
        int accessOrdinal,
        int view,
        int sampler = -1)
    {
        ref PassData graphPass = ref GetPass(pass);
        AppendShaderArgument(
            ref graphPass,
            group,
            binding,
            element,
            type,
            accessOrdinal,
            view,
            sampler);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendShaderArgument(
        ref PassData graphPass,
        uint group,
        uint binding,
        uint element,
        GraphBindingType type,
        int accessOrdinal,
        int view,
        int sampler = -1)
    {
        if (_declarationPass < 0)
            throw new InvalidOperationException("No generated declaration range is active.");
        int ordinal = _declarationShaderArgumentCursor++;
        if (ordinal >= _declarationShaderArgumentEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more shader arguments than it reserved.");
        if (ordinal >= _declarationShaderArgumentEnd)
        {
            _ = _shaderArgumentGroups.AddUninitialized(1);
            _ = _shaderArgumentBindings.AddUninitialized(1);
            _ = _shaderArgumentElements.AddUninitialized(1);
            _ = _shaderArgumentTypes.AddUninitialized(1);
            _ = _shaderArgumentAccesses.AddUninitialized(1);
            _ = _shaderArgumentViews.AddUninitialized(1);
            _ = _shaderArgumentSamplers.AddUninitialized(1);
            _declarationShaderArgumentEnd++;
        }
        _shaderArgumentGroups[ordinal] = group;
        _shaderArgumentBindings[ordinal] = binding;
        _shaderArgumentElements[ordinal] = element;
        _shaderArgumentTypes[ordinal] = type;
        _shaderArgumentAccesses[ordinal] = accessOrdinal;
        _shaderArgumentViews[ordinal] = view;
        _shaderArgumentSamplers[ordinal] = sampler;
        graphPass.ShaderArgumentCount++;
    }

    private void AddQueryPool(int pass, QueryPoolHandle query)
    {
        EnsureAuthoring();
        _ = GetQueryPool(query);
        ref PassData passRow = ref GetPass(pass);
        foreach (int value in GetPassQueries(passRow))
            if (value == query.Ordinal)
                throw new InvalidOperationException($"Pass '{GetPassName(pass)}' already declares query pool {query.Ordinal}.");
        if (_declarationPass != pass)
            throw new InvalidOperationException("No generated declaration range is active for this pass.");
        int ordinal = _declarationQueryCursor++;
        if (ordinal >= _declarationQueryEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more query accesses than it reserved.");
        if (ordinal >= _declarationQueryEnd)
        {
            _ = _passQueries.AddUninitialized(1);
            _declarationQueryEnd++;
        }
        _passQueries[ordinal] = query.Ordinal;
        passRow.QueryAccessCount++;
    }

    internal string GetPassName(int pass) => _passNames[pass];

    internal string? GetBufferViewName(int view) =>
        (uint)view < (uint)_bufferViewNames.Count ? _bufferViewNames[view] : null;

    internal string? GetTextureViewName(int view) =>
        (uint)view < (uint)_textureViewNames.Count ? _textureViewNames[view] : null;

    internal void Close()
    {
        EnsureAuthoring();
        if (_openPass >= 0)
            throw new InvalidOperationException("A pass is still being authored.");
        for (int index = 0; index < _passes.Count; index++)
        {
            if (_passExecutors[index] is null)
                throw new InvalidOperationException($"Pass '{GetPassName(index)}' has no command encoding operation.");
        }
        _consumed = true;
    }

    private GraphBindingType TextureBindingKind(int view)
    {
        GraphTextureViewUsage usage = _textureViewUsages[view];
        bool shaderResource = (usage & GraphTextureViewUsage.ShaderResource) != 0;
        bool storage = (usage & GraphTextureViewUsage.Storage) != 0;
        if (shaderResource == storage)
            throw new InvalidOperationException("A bindless graph texture view must select exactly one shader descriptor usage.");
        return shaderResource ? GraphBindingType.SampledTexture : GraphBindingType.StorageTexture;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveBuffer(BufferHandle resource)
    {
        ValidateResource(resource.Graph, resource.Ordinal, _buffers.Count);
        return resource.Ordinal;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveTexture(TextureHandle resource)
    {
        ValidateResource(resource.Graph, resource.Ordinal, _textures.Count);
        return resource.Ordinal;
    }

    private int AddBufferAccessCore(
        int pass,
        int resource,
        int view,
        GraphAccess flags,
        GraphResourceUsage use,
        BufferRange range)
    {
        return AppendBufferInput(
            pass,
            resource,
            view,
            flags,
            use,
            range);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AppendBufferInput(
        int pass,
        int resource,
        int view,
        GraphAccess flags,
        GraphResourceUsage use,
        BufferRange range)
    {
        ValidateBufferEffect(flags, use);
        ref PassData passRow = ref GetPass(pass);
        PassInputData row = new(
            resource,
            view,
            flags,
            use,
            range);
        return AppendCanonicalAccess(pass, ref passRow, row);
    }

    private int AddTextureAccessCore(
        int pass,
        int resource,
        int view,
        GraphAccess flags,
        GraphResourceUsage use,
        TextureSubresourceRange range)
    {
        return AppendTextureInput(
            pass,
            resource,
            view,
            flags,
            use,
            range);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int AppendTextureInput(
        int pass,
        int resource,
        int view,
        GraphAccess flags,
        GraphResourceUsage use,
        TextureSubresourceRange range)
    {
        ValidateTextureEffect(flags, use);
        ref PassData passRow = ref GetPass(pass);
        PassInputData row = new(
            resource,
            view,
            flags,
            use,
            range);
        return AppendCanonicalAccess(pass, ref passRow, row);
    }

    private int AppendCanonicalAccess(int pass, ref PassData passRow, in PassInputData row)
    {
        if (_declarationPass != pass)
            throw new InvalidOperationException("No generated declaration range is active for this pass.");
        int canonicalAccess = passRow.AccessCount;
        int ordinal = _declarationAccessCursor++;
        if (ordinal >= _declarationAccessEnd && !_dynamicDeclarations)
            throw new InvalidOperationException("The generated pass wrote more canonical accesses than it reserved.");
        if (ordinal >= _declarationAccessEnd)
        {
            _ = _accesses.AddUninitialized(1);
            _ = _accessPredecessors.AddUninitialized(1);
            _declarationAccessEnd++;
        }
        int predecessor = ValidateAndIndexCanonicalAccess(pass, ordinal, in row);
        _accesses[ordinal] = row;
        _accessPredecessors[ordinal] = predecessor;
        passRow.AccessCount++;
        return canonicalAccess;
    }

    private int ValidateAndIndexCanonicalAccess(int pass, int ordinal, in PassInputData row)
    {
        ref PassAccessHead head = ref row.IsBuffer
            ? ref _bufferAccessHeads[row.Buffer]
            : ref _textureAccessHeads[row.Texture];
        int stamp = checked(pass + 1);
        int predecessor = head.PassStamp == stamp ? head.Access : -1;
        for (int candidate = predecessor;
             candidate >= 0;
             candidate = _accessPredecessors[candidate])
        {
            ref PassInputData prior = ref _accesses[candidate];
            if (!AccessNormalizer.Overlaps(row, prior) ||
                AccessNormalizer.IsReadOnlyDepthLocalRead(row, prior))
            {
                continue;
            }
            throw new InvalidOperationException(
                $"Pass '{GetPassName(pass)}' declares overlapping accesses to one resource; declare one joined ReadWrite access instead.");
        }
        head = new PassAccessHead(stamp, ordinal);
        return predecessor;
    }

    private readonly record struct PassAccessHead(int PassStamp, int Access);

    private int AddAttachmentPlaneAccess(
        int pass,
        int view,
        GraphTextureAspect plane,
        LoadType load,
        bool readOnly)
    {
        TextureSubresourceRange range = _textureViewRanges[view] with
        {
            Aspects = plane switch
            {
                GraphTextureAspect.Color => TextureAspects.Color,
                GraphTextureAspect.Depth => TextureAspects.Depth,
                GraphTextureAspect.Stencil => TextureAspects.Stencil,
                _ => throw new ArgumentOutOfRangeException(nameof(plane)),
            },
        };
        GraphAccess flags = readOnly
            ? GraphAccess.Read
            : load == LoadType.Load
                ? GraphAccess.Write
                : GraphAccess.WriteAll;
        GraphResourceUsage use = readOnly ? GraphResourceUsage.DepthRead : GraphResourceUsage.DepthWrite;
        return AddTextureAccessCore(
            pass,
            _textureViewResources[view],
            view,
            flags,
            use,
            range);
    }

    private static void ValidateDepthAttachment(
        LoadType load,
        bool readOnly,
        float clearDepth)
    {
        if (!Enum.IsDefined(load)) throw new ArgumentOutOfRangeException(nameof(load));
        if (readOnly && load != LoadType.Load)
            throw new ArgumentException("A read-only depth plane requires Load.", nameof(readOnly));
        if (clearDepth is < 0f or > 1f || float.IsNaN(clearDepth))
            throw new ArgumentOutOfRangeException(nameof(clearDepth), "Depth clear value must be in [0, 1].");
    }

    private static void ValidateStencilAttachment(
        LoadType load,
        bool readOnly)
    {
        if (!Enum.IsDefined(load)) throw new ArgumentOutOfRangeException(nameof(load));
        if (readOnly && load != LoadType.Load)
            throw new ArgumentException("A read-only stencil plane requires Load.", nameof(readOnly));
    }

    private ref PassData GetPass(int pass)
    {
        if ((uint)pass >= (uint)_passes.Count) throw new ArgumentOutOfRangeException(nameof(pass));
        return ref _passes[pass];
    }

    private void ValidateResource(long graph, int ordinal, int count)
    {
        if (graph != GraphSerial)
            throw new ArgumentException("The resource belongs to a different graph invocation.");
        if ((uint)ordinal >= (uint)count)
            throw new ArgumentException("The resource handle has an invalid ordinal.");
    }

    private int ValidateBufferView(BufferViewHandle view)
    {
        if (view.Graph != GraphSerial)
            throw new ArgumentException("The buffer view belongs to a different graph invocation.", nameof(view));
        if ((uint)view.Ordinal >= (uint)_bufferViewResources.Count)
            throw new ArgumentException("The buffer view handle has an invalid ordinal.", nameof(view));
        return view.Ordinal;
    }

    private int ValidateAccelerationStructure(AccelerationStructureHandle accelerationStructure)
    {
        if (accelerationStructure.Graph != GraphSerial)
            throw new ArgumentException(
                "The acceleration structure belongs to a different graph invocation.",
                nameof(accelerationStructure));
        if ((uint)accelerationStructure.Ordinal >= (uint)_accelerationStructureBuffers.Count)
            throw new ArgumentException(
                "The acceleration-structure handle has an invalid ordinal.",
                nameof(accelerationStructure));
        return accelerationStructure.Ordinal;
    }

    private int ValidateTextureView(TextureViewHandle view)
    {
        if (view.Graph != GraphSerial)
            throw new ArgumentException("The texture view belongs to a different graph invocation.", nameof(view));
        if ((uint)view.Ordinal >= (uint)_textureViewResources.Count)
            throw new ArgumentException("The texture view handle has an invalid ordinal.", nameof(view));
        return view.Ordinal;
    }

    private void ValidateViewAccess(
        int pass,
        int accessOrdinal,
        GraphBindingType type,
        int view)
    {
        ref PassData graphPass = ref GetPass(pass);
        if ((uint)accessOrdinal >= (uint)GetPassAccesses(graphPass).Length)
            throw new ArgumentException("The view access ordinal does not belong to this pass.", nameof(accessOrdinal));
        PassInputData expected = GetDeclaredAccess(pass, accessOrdinal);
        if (expected.View != view)
            throw new ArgumentException("The view access ordinal does not match the canonical access.", nameof(view));
        bool matches = type switch
        {
            GraphBindingType.ConstantBuffer or
            GraphBindingType.ReadOnlyBuffer or
            GraphBindingType.StorageBuffer =>
                expected.IsBuffer &&
                (uint)view < (uint)_bufferViewResources.Count &&
                expected.Buffer == _bufferViewResources[view] &&
                _bufferViewTypes[view] == type,
            GraphBindingType.AccelerationStructure =>
                expected.IsBuffer &&
                expected.State == GraphResourceUsage.AccelerationStructure &&
                (uint)view < (uint)_accelerationStructureBuffers.Count &&
                expected.Buffer == _accelerationStructureBuffers[view],
            GraphBindingType.SampledTexture or
            GraphBindingType.StorageTexture =>
                !expected.IsBuffer &&
                (uint)view < (uint)_textureViewResources.Count &&
                expected.Texture == _textureViewResources[view] &&
                TextureBindingKind(view) == type,
            _ => false,
        };
        if (!matches)
            throw new ArgumentException("The descriptor type does not match its canonical access.", nameof(type));
    }

    private void MarkViewMaterialization(ref PassData pass, int access)
    {
        ReadOnlySpan<PassInputData> accesses = GetPassAccesses(pass);
        if ((uint)access >= (uint)accesses.Length || accesses[access].View < 0)
            throw new ArgumentException("Only an exact declared view access can be materialized.", nameof(access));
    }

    private void EnsureAuthoring()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Graph authoring is single-writer.");
        if (_consumed || _disposed) throw new InvalidOperationException("The render graph invocation has already executed or been disposed.");
    }

    private static void ValidateBufferEffect(GraphAccess flags, GraphResourceUsage use)
    {
        GraphAccess effect = flags & GraphAccess.ReadWrite;
        if ((flags & ~(GraphAccess.ReadWrite | GraphAccess.Discard)) != 0 ||
            effect == GraphAccess.None ||
            (flags & GraphAccess.Discard) != 0 && effect != GraphAccess.Write)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        if ((uint)use > (uint)GraphResourceUsage.AccelerationStructure) throw new ArgumentOutOfRangeException(nameof(use));
        bool writable = use is GraphResourceUsage.CopyDestination or GraphResourceUsage.UnorderedAccess or GraphResourceUsage.AccelerationStructure;
        bool readable = use != GraphResourceUsage.CopyDestination;
        if (effect == GraphAccess.Read && !readable) throw new ArgumentException($"Buffer use '{use}' does not permit read access.");
        if (effect == GraphAccess.Write && !writable) throw new ArgumentException($"Buffer use '{use}' does not permit write access.");
        if (effect == GraphAccess.ReadWrite && use is not (GraphResourceUsage.UnorderedAccess or GraphResourceUsage.AccelerationStructure))
            throw new ArgumentException("ReadWrite buffer access requires ShaderWrite or AccelerationStructure use.");
    }

    private static void ValidateTextureEffect(GraphAccess flags, GraphResourceUsage use)
    {
        GraphAccess effect = flags & GraphAccess.ReadWrite;
        if ((flags & ~(GraphAccess.ReadWrite | GraphAccess.Discard)) != 0 ||
            effect == GraphAccess.None ||
            (flags & GraphAccess.Discard) != 0 && effect != GraphAccess.Write)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        if ((uint)use > (uint)GraphResourceUsage.ShadingRateSource) throw new ArgumentOutOfRangeException(nameof(use));
        bool writeUse = use is GraphResourceUsage.CopyDestination or GraphResourceUsage.ResolveDestination or GraphResourceUsage.UnorderedAccess or GraphResourceUsage.RenderTarget or GraphResourceUsage.DepthWrite;
        bool readUse = use is GraphResourceUsage.CopySource or GraphResourceUsage.ResolveSource or GraphResourceUsage.ShaderResource or GraphResourceUsage.UnorderedAccess or GraphResourceUsage.DepthRead or GraphResourceUsage.ShadingRateSource;
        if (effect == GraphAccess.Read && !readUse) throw new ArgumentException($"Texture use '{use}' does not permit read access.");
        if (effect == GraphAccess.Write && !writeUse) throw new ArgumentException($"Texture use '{use}' does not permit write access.");
        if (effect == GraphAccess.ReadWrite && use != GraphResourceUsage.UnorderedAccess) throw new ArgumentException("ReadWrite texture access requires Storage use.");
    }

    private static void ValidateBufferView(
        BufferUsages usage,
        GraphBindingType kind,
        Format? format,
        uint stride,
        in BufferRange range)
    {
        BufferUsages required = kind switch
        {
            GraphBindingType.ConstantBuffer => BufferUsages.Constant,
            GraphBindingType.ReadOnlyBuffer => BufferUsages.ShaderRead,
            GraphBindingType.StorageBuffer => BufferUsages.ShaderWrite,
            _ => throw new ArgumentException($"Binding kind {kind} cannot describe a buffer view.", nameof(kind)),
        };
        if ((usage & required) == 0) throw new ArgumentException($"Buffer view kind {kind} requires resource usage {required}.", nameof(kind));
        if (stride > range.Size) throw new ArgumentOutOfRangeException(nameof(stride));
        if (format is Format typedFormat && !GraphFormat.IsDefined(typedFormat))
            throw new ArgumentOutOfRangeException(nameof(format));
    }

    private static void ValidateViewEffect(GraphAccess flags, GraphBindingType kind, string parameterName)
    {
        GraphAccess effect = flags & GraphAccess.ReadWrite;
        if ((flags & ~(GraphAccess.ReadWrite | GraphAccess.Discard)) != 0 ||
            effect == GraphAccess.None ||
            (flags & GraphAccess.Discard) != 0 && effect != GraphAccess.Write)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        bool writable = kind is GraphBindingType.StorageBuffer or GraphBindingType.StorageTexture;
        if (effect != GraphAccess.Read && !writable)
            throw new ArgumentException($"View kind {kind} is read-only.", parameterName);
    }
}

internal readonly record struct PassRollbackMarker(
    int Passes,
    int Accesses,
    int AccessPredecessors,
    int ColorAttachments,
    int DepthStencilAttachments,
    int ShaderArguments,
    int Queries,
    int BindlessAccesses,
    int PassNames,
    int PassPipelines,
    int ParameterLayouts,
    int ParameterOrdinaryData);
