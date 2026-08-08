namespace SomeEngine.RenderGraph;

using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public sealed partial class RenderGraph
{
    internal ArenaSlice<T> AllocateSlice<T>(int count, bool clear = true) where T : unmanaged =>
        _arena.AllocateSlice<T>(count, clear);

    internal ArenaColumn<T> CreateArenaColumn<T>(
        int capacity = 0) where T : unmanaged =>
        new(_arena, capacity);

    internal ArenaColumn<ResourceUnversionedData> Buffers => _buffers;
    internal ArenaColumn<ResourceUnversionedData> Textures => _textures;
    internal int BufferViewCount => _bufferViewResources.Count;
    internal int GetBufferViewResource(int ordinal) => _bufferViewResources[ordinal];
    internal BufferRange GetBufferViewRange(int ordinal) => _bufferViewRanges[ordinal];
    internal GraphBindingType GetBufferViewType(int ordinal) => _bufferViewTypes[ordinal];
    internal Format? GetBufferViewFormat(int ordinal) =>
        _bufferViewFormats[ordinal] == default ? null : _bufferViewFormats[ordinal];
    internal uint GetBufferViewStride(int ordinal) => _bufferViewStrides[ordinal];
    internal int TextureViewCount => _textureViewResources.Count;
    internal int GetTextureViewResource(int ordinal) => _textureViewResources[ordinal];
    internal TextureSubresourceRange GetTextureViewRange(int ordinal) => _textureViewRanges[ordinal];
    internal GraphTextureViewUsage GetTextureViewUsage(int ordinal) => _textureViewUsages[ordinal];
    internal Format GetTextureViewFormat(int ordinal) => _textureViewFormats[ordinal];
    internal TextureViewDimension GetTextureViewDimension(int ordinal) =>
        _textureViewDimensions[ordinal];
    internal int AccelerationStructureCount => _accelerationStructureBuffers.Count;
    internal int GetAccelerationStructureBuffer(int ordinal) =>
        _accelerationStructureBuffers[ordinal];
    internal BufferRange GetAccelerationStructureRange(int ordinal) =>
        _accelerationStructureRanges[ordinal];
    internal AccelerationStructureType GetAccelerationStructureType(int ordinal) =>
        _accelerationStructureTypes[ordinal];
    internal ArenaColumn<PassData> Passes => _passes;
    internal ArenaColumn<PassInputData> PassInputs => _accesses;
    internal int ShaderArgumentCount => _shaderArgumentTypes.Count;
    internal uint GetShaderArgumentGroup(int ordinal) =>
        _shaderArgumentGroups[ordinal];
    internal uint GetShaderArgumentBinding(int ordinal) =>
        _shaderArgumentBindings[ordinal];
    internal uint GetShaderArgumentElement(int ordinal) =>
        _shaderArgumentElements[ordinal];
    internal GraphBindingType GetShaderArgumentType(int ordinal) =>
        _shaderArgumentTypes[ordinal];
    internal int GetShaderArgumentAccess(int ordinal) =>
        _shaderArgumentAccesses[ordinal];
    internal int GetShaderArgumentView(int ordinal) =>
        _shaderArgumentViews[ordinal];
    internal int GetShaderArgumentSampler(int ordinal) =>
        _shaderArgumentSamplers[ordinal];
    internal int BindlessAccessCount => _bindlessAccessTypes.Count;
    internal int GetBindlessAccessTable(int ordinal) =>
        _bindlessAccessTables[ordinal];
    internal GraphBindingType GetBindlessAccessType(int ordinal) =>
        _bindlessAccessTypes[ordinal];
    internal int GetBindlessAccess(int ordinal) =>
        _bindlessAccesses[ordinal];
    internal int GetBindlessAccessView(int ordinal) =>
        _bindlessAccessViews[ordinal];
    internal ArenaColumn<int> PassQueries => _passQueries;
    internal void MakeCanonicalSlicesContiguous()
    {
        _accesses.MakeContiguous();
        _colorAttachments.MakeContiguous();
        _passQueries.MakeContiguous();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<PassInputData> GetPassAccesses(PassData pass) =>
        _accesses.GetReadOnlySpan(pass.AccessOffset, pass.AccessCount);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<PassInputData> GetPassInputs(PassData pass) =>
        _accesses.GetSpan(pass.AccessOffset, pass.AccessCount);
    internal unsafe ReadOnlySpan<int> GetPassDependencies(int passOrdinal)
    {
        PassData* rows = _passes.DangerousContiguousPointer;
        PassData row = rows is not null
            ? rows[passOrdinal]
            : _passes[passOrdinal];
        return DependencyRows.GetReadOnlySpan(row.DependencyOffset, row.DependencyCount);
    }
    internal unsafe ReadOnlySpan<PlannedBarrier> GetBeforeBarriers(int passOrdinal)
    {
        PassData* rows = _passes.DangerousContiguousPointer;
        PassData row = rows is not null
            ? rows[passOrdinal]
            : _passes[passOrdinal];
        return BeforeResourceBarriers.GetReadOnlySpan(row.BeforeBarrierOffset, row.BeforeBarrierCount);
    }
    internal unsafe ReadOnlySpan<PlannedBarrier> GetAfterBarriers(int passOrdinal)
    {
        PassData* rows = _passes.DangerousContiguousPointer;
        PassData row = rows is not null
            ? rows[passOrdinal]
            : _passes[passOrdinal];
        return AfterResourceBarriers.GetReadOnlySpan(row.AfterBarrierOffset, row.AfterBarrierCount);
    }
    internal ReadOnlySpan<PassFragmentData> GetPassColorAttachments(PassData pass) =>
        _colorAttachments.GetReadOnlySpan(pass.ColorAttachmentOffset, pass.ColorAttachmentCount);
    internal PassFragmentData? GetPassDepthStencilAttachment(PassData pass) =>
        pass.DepthStencilAttachmentOrdinal < 0
            ? null
            : _depthStencilAttachments[pass.DepthStencilAttachmentOrdinal];
    internal Span<PassFragmentData> GetPassColorAttachmentRows(PassData pass) =>
        _colorAttachments.GetSpan(pass.ColorAttachmentOffset, pass.ColorAttachmentCount);
    internal ReadOnlySpan<int> GetPassQueries(PassData pass) =>
        _passQueries.GetReadOnlySpan(pass.QueryAccessOffset, pass.QueryAccessCount);
    internal ReadOnlySpan<int> GetBatchDependencies(CommandBatch batch) =>
        BatchDependencyRows.GetReadOnlySpan(batch.DependencyOffset, batch.DependencyCount);
    internal ReadOnlySpan<int> GetBatchCommandUnits(CommandBatch batch) =>
        BatchRuntimeCmds.GetReadOnlySpan(batch.CommandUnitOffset, batch.CommandUnitCount);
    internal ReadOnlySpan<int> GetBatchResources(CommandBatch batch) =>
        BatchResourceRows.GetReadOnlySpan(batch.ResourceOffset, batch.ResourceCount);
    internal ReadOnlySpan<QueueCompletion> GetBatchExternalWaits(CommandBatch batch) =>
        BatchExternalWaitRows.ReadOnlySpan.Slice(
            batch.ExternalWaitOffset,
            batch.ExternalWaitCount);
    internal unsafe ReadOnlySpan<int> GetCommandUnitDependencies(int unit)
    {
        RuntimeCmd* rows =
            CommandUnits.DangerousContiguousPointer;
        RuntimeCmd row = rows is not null
            ? rows[unit]
            : CommandUnits[unit];
        return CommandUnitDependencyRows.GetReadOnlySpan(
            row.DependencyOffset,
            row.DependencyCount);
    }
    internal ReadOnlySpan<int> GetCommandUnitPasses(RuntimeCmd unit) =>
        CommandUnitPassRows.GetReadOnlySpan(unit.PassOffset, unit.PassCount);
    internal ReadOnlySpan<PlannedAliasingBarrier> GetCommandUnitAliases(RuntimeCmd unit) =>
        CommandUnitAliasRows.GetReadOnlySpan(unit.AliasOffset, unit.AliasCount);
    internal ReadOnlySpan<PlannedBarrier> GetCommandUnitBarriers(RuntimeCmd unit) =>
        CommandUnitResourceBarriers.GetReadOnlySpan(unit.BarrierOffset, unit.BarrierCount);

    internal int ResourceCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffers.Count + _textures.Count;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetBufferResourceOrdinal(int buffer) => buffer;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetTextureResourceOrdinal(int texture) =>
        checked(_buffers.Count + texture);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetResourceOrdinal(in PassInputData access) => access.IsBuffer
        ? access.Buffer
        : GetTextureResourceOrdinal(access.Texture);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsBufferResourceOrdinal(int ordinal) => (uint)ordinal < (uint)_buffers.Count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetBufferOrdinal(int resourceOrdinal) =>
        (uint)resourceOrdinal < (uint)_buffers.Count
            ? resourceOrdinal
            : throw new ArgumentOutOfRangeException(nameof(resourceOrdinal));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int GetTextureOrdinal(int resourceOrdinal) => checked(resourceOrdinal - _buffers.Count);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe ref ResourceUnversionedData GetBufferByResourceOrdinal(int ordinal)
    {
        ResourceUnversionedData* rows = _buffers.DangerousContiguousPointer;
        return ref (rows is not null
            ? ref rows[ordinal]
            : ref _buffers[ordinal]);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe ref ResourceUnversionedData GetTextureByResourceOrdinal(int ordinal)
    {
        int texture = GetTextureOrdinal(ordinal);
        ResourceUnversionedData* rows = _textures.DangerousContiguousPointer;
        return ref (rows is not null
            ? ref rows[texture]
            : ref _textures[texture]);
    }
    internal unsafe bool IsResourceImported(int ordinal)
    {
        if (IsBufferResourceOrdinal(ordinal))
        {
            ResourceUnversionedData* buffers = _buffers.DangerousContiguousPointer;
            return (buffers is not null
                ? buffers[ordinal]
                : _buffers[ordinal]).IsImported;
        }
        int texture = GetTextureOrdinal(ordinal);
        ResourceUnversionedData* textures = _textures.DangerousContiguousPointer;
        return (textures is not null
            ? textures[texture]
            : _textures[texture]).IsImported;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe bool IsResourceLive(int ordinal) =>
        (LivenessFlags.DangerousPointer[Passes.Length + ordinal] &
         ResourceLiveFlag) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe bool IsResourceWritten(int ordinal) =>
        (LivenessFlags.DangerousPointer[Passes.Length + ordinal] &
         ResourceWrittenFlag) != 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal GraphMemoryRequirements GetResourceRequirements(int ordinal) =>
        ResourceRequirementRows[ordinal];
    internal ReadOnlySpan<QueueCompletion> GetResourceReadiness(int ordinal)
    {
        ResourceUnversionedData resource = IsBufferResourceOrdinal(ordinal)
            ? _buffers[ordinal]
            : _textures[GetTextureOrdinal(ordinal)];
        return GetImportReadiness(resource.ReadinessOffset, resource.ReadinessCount);
    }

    internal ref BufferDesc GetBufferDescription(int ordinal) =>
        ref _bufferDescriptions[ordinal];

    internal ref GraphTextureDescription GetTextureDescription(int ordinal) => ref _textureDescriptions[ordinal];

    internal Buffer GetImportedBuffer(in ResourceUnversionedData resource)
    {
        if (!resource.IsImported)
            throw new InvalidOperationException("The graph buffer is not imported.");
        return (Buffer)_importedResources[resource.ImportOrdinal];
    }

    internal Texture GetImportedTexture(in ResourceUnversionedData resource)
    {
        if (!resource.IsImported)
            throw new InvalidOperationException("The graph texture is not imported.");
        return (Texture)_importedResources[resource.ImportOrdinal];
    }

    internal SwapchainImage GetSwapchainImage(in ResourceUnversionedData resource)
    {
        if (resource.SwapchainImageOrdinal < 0)
            throw new InvalidOperationException("The graph texture is not a swapchain image.");
        return _swapchainImages[resource.SwapchainImageOrdinal];
    }

    internal ReadOnlySpan<QueueCompletion> GetImportReadiness(int offset, int count) =>
        count == 0 ? [] : _importReadinessRows.ReadOnlySpan.Slice(offset, count);

    internal ReadOnlySpan<byte> GetBufferInitialData(in ResourceUnversionedData row)
    {
        if (row.InitialDataAddress >= 0)
            return _arena.GetBytes(
                row.InitialDataAddress,
                row.InitialDataLength);
        return row.InitialDataOrdinal < 0
            ? []
            : _bufferInitialData[row.InitialDataOrdinal].Span;
    }

    internal PassInputData GetDeclaredAccess(int passOrdinal, int accessOrdinal)
    {
        PassData pass = Passes[passOrdinal];
        ReadOnlySpan<PassInputData> accesses = GetPassAccesses(pass);
        if ((uint)accessOrdinal >= (uint)accesses.Length)
            throw new ArgumentOutOfRangeException(nameof(accessOrdinal));
        return accesses[accessOrdinal];
    }
}

internal struct ReferenceColumn<T>
{
    private T[] _items;
    private int _count;

    internal ReferenceColumn(int capacity = 0)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = capacity == 0 ? [] : ArrayPool<T>.Shared.Rent(capacity);
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }
    internal Span<T> Span => _items.AsSpan(0, _count);
    internal ReadOnlySpan<T> ReadOnlySpan => _items.AsSpan(0, _count);
    internal ReadOnlyMemory<T> ReadOnlyMemory => _items.AsMemory(0, _count);
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _items[index];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(T value)
    {
        int index = _count;
        if ((uint)index >= (uint)_items.Length) Grow(checked(index + 1));
        _items[index] = value;
        _count = index + 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> AddUninitialized(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int offset = _count;
        EnsureCapacity(checked(offset + count));
        _count += count;
        return _items.AsSpan(offset, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length) return;
        Grow(capacity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int capacity)
    {
        int doubled = _items.Length == 0 ? 4 : checked(_items.Length * 2);
        int target = Math.Max(capacity, doubled);
        T[] replacement = ArrayPool<T>.Shared.Rent(target);
        _items.AsSpan(0, _count).CopyTo(replacement);
        if (_items.Length != 0)
            ArrayPool<T>.Shared.Return(_items, clearArray: true);
        _items = replacement;
    }

    internal void AddDefault(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        EnsureCapacity(checked(_count + count));
        _items.AsSpan(_count, count).Clear();
        _count += count;
    }

    internal void RemoveRange(int index, int count)
    {
        if ((uint)index > (uint)_count || (uint)count > (uint)(_count - index))
            throw new ArgumentOutOfRangeException(nameof(count));
        int tail = _count - index - count;
        if (tail != 0) _items.AsSpan(index + count, tail).CopyTo(_items.AsSpan(index));
        _count -= count;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _items.AsSpan(_count, count).Clear();
    }

    internal void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) _items.AsSpan(0, _count).Clear();
        _count = 0;
    }

    internal void Dispose()
    {
        Clear();
        if (_items.Length == 0)
            return;
        ArrayPool<T>.Shared.Return(_items, clearArray: true);
        _items = [];
    }

}

/// <summary>
/// Dense logical column backed by fixed-size arena chunks. Appending never moves a previously
/// stored row; range reservations keep each canonical per-pass/per-unit slice in one chunk.
/// </summary>
internal unsafe struct ArenaColumn<T> : IReadOnlyList<T> where T : unmanaged
{
    private readonly GraphArena _arena;
    private Chunk* _first;
    private Chunk* _last;
    private int _count;

    private struct Chunk
    {
        internal Chunk* Previous;
        internal Chunk* Next;
        internal T* Items;
        internal int Start;
        internal int Count;
        internal int Capacity;
    }

    internal ArenaColumn(GraphArena arena, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _arena = arena;
        _first = null;
        _last = null;
        _count = 0;
        if (capacity != 0) EnsureCapacity(capacity);
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }
    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }
    internal T* DangerousContiguousPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _first is not null &&
               _first == _last &&
               _first->Count == _count
            ? _first->Items
            : null;
    }

    /// <summary>
    /// Rehomes the logical column into one arena allocation without changing row ordinals.
    /// Authoring may append an unknown number of rows one at a time, so a canonical
    /// per-pass range can otherwise straddle two growth chunks even though compilation
    /// consumes that range as one span.
    /// </summary>
    internal void MakeContiguous()
    {
        if (_count == 0 || _first == _last)
            return;

        ArenaColumn<T> compacted = new(_arena, _count);
        Span<T> destination = compacted.AddUninitialized(_count);
        int offset = 0;
        for (Chunk* chunk = _first; chunk is not null; chunk = chunk->Next)
        {
            new ReadOnlySpan<T>(chunk->Items, chunk->Count)
                .CopyTo(destination[offset..]);
            offset += chunk->Count;
        }
        this = compacted;
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));
            Chunk* chunk = _last;
            if (chunk is not null && index >= chunk->Start)
                return ref chunk->Items[index - chunk->Start];
            chunk = _first;
            while (chunk is not null &&
                   index >= chunk->Start + chunk->Count)
            {
                chunk = chunk->Next;
            }
            if (chunk is null)
                throw new InvalidOperationException(
                    "Arena-column row lookup failed.");
            return ref chunk->Items[index - chunk->Start];
        }
    }
    T IReadOnlyList<T>.this[int index] => this[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(T value)
    {
        EnsureAppendCapacity(1);
        _last->Items[_last->Count++] = value;
        _count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> AddUninitialized(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return default;
        EnsureAppendCapacity(count);
        Span<T> result = new(_last->Items + _last->Count, count);
        _last->Count += count;
        _count += count;
        return result;
    }

    internal void AddRange(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is ICollection<T> collection) EnsureAppendCapacity(collection.Count);
        foreach (T value in values) Add(value);
    }

    /// <summary>Ensures the next rows through <paramref name="capacity"/> fit one stable chunk.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureCapacity(int capacity)
    {
        if (capacity < _count) return;
        EnsureAppendCapacity(capacity - _count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnsureAppendCapacity(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        if (_last is not null && _last->Capacity - _last->Count >= count) return;
        AllocateChunk(count);
    }

    internal void AddDefault(int count)
    {
        Span<T> rows = AddUninitialized(count);
        rows.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> GetSpan(int offset, int count)
    {
        if ((uint)offset > (uint)_count || (uint)count > (uint)(_count - offset))
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return default;
        Chunk* chunk = _last;
        if (chunk is null || offset < chunk->Start)
        {
            chunk = _first;
            while (chunk is not null &&
                   offset >= chunk->Start + chunk->Count)
            {
                chunk = chunk->Next;
            }
            if (chunk is null)
                throw new InvalidOperationException(
                    "Arena-column row lookup failed.");
        }
        int local = offset - chunk->Start;
        if (count > chunk->Count - local)
            throw new InvalidOperationException("A canonical row slice crossed an arena-column chunk boundary.");
        return new Span<T>(chunk->Items + local, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<T> GetReadOnlySpan(int offset, int count) => GetSpan(offset, count);

    internal bool Contains(T value)
    {
        for (Chunk* chunk = _first; chunk is not null; chunk = chunk->Next)
            if (new ReadOnlySpan<T>(chunk->Items, chunk->Count).Contains(value)) return true;
        return false;
    }

    internal void RemoveRange(int index, int count)
    {
        if ((uint)index > (uint)_count || (uint)count > (uint)(_count - index))
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        if (index + count != _count)
            throw new InvalidOperationException("Arena columns support transactional tail rollback only.");
        int target = index;
        while (_last is not null && target <= _last->Start)
        {
            Chunk* discarded = _last;
            _last = discarded->Previous;
            if (_last is null) _first = null;
            else _last->Next = null;
        }
        if (_last is not null)
            _last->Count = target - _last->Start;
        _count = target;
    }

    internal void Clear()
    {
        _first = null;
        _last = null;
        _count = 0;
    }

    internal void Sort(Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        for (int index = 1; index < _count; index++)
        {
            T value = this[index];
            int destination = index;
            while (destination > 0 && comparison(this[destination - 1], value) > 0)
            {
                this[destination] = this[destination - 1];
                destination--;
            }
            this[destination] = value;
        }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void AllocateChunk(int required)
    {
        int defaultCapacity = Math.Max(64, 4096 / Math.Max(1, sizeof(T)));
        int capacity = Math.Max(defaultCapacity, required);
        ArenaSlice<Chunk> header = _arena.AllocateSlice<Chunk>(1, clear: false);
        ArenaSlice<T> items = _arena.AllocateSlice<T>(capacity, clear: false);
        Chunk* chunk = (Chunk*)Unsafe.AsPointer(ref header[0]);
        chunk->Previous = _last;
        chunk->Next = null;
        chunk->Items = (T*)Unsafe.AsPointer(ref items[0]);
        chunk->Start = _count;
        chunk->Count = 0;
        chunk->Capacity = capacity;
        if (_last is null) _first = chunk;
        else _last->Next = chunk;
        _last = chunk;
    }

    public struct Enumerator : IEnumerator<T>
    {
        private readonly ArenaColumn<T> _column;
        private int _index;

        internal Enumerator(ArenaColumn<T> column)
        {
            _column = column;
            _index = -1;
        }

        public T Current => _column[_index];
        object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _column._count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}

internal struct ResourceUnversionedData
{
    internal ResourceUnversionedData(MemoryType memoryType)
    {
        MemoryType = memoryType;
        ImportOrdinal = -1;
        InitialState = GraphResourceUsage.Common;
        FinalState = GraphResourceUsage.Common;
        ContentsAvailable = false;
        ReadinessOffset = 0;
        ReadinessCount = 0;
        InitialDataOrdinal = -1;
        InitialDataAddress = -1;
        InitialDataLength = 0;
        SwapchainImageOrdinal = -1;
    }

    internal ResourceUnversionedData(
        MemoryType memoryType,
        int importOrdinal,
        GraphResourceUsage initialState,
        GraphResourceUsage finalState,
        bool contentsAvailable,
        int readinessOffset,
        int readinessCount,
        int swapchainImageOrdinal = -1)
        : this(memoryType)
    {
        ImportOrdinal = importOrdinal;
        InitialState = initialState;
        FinalState = finalState;
        ContentsAvailable = contentsAvailable;
        ReadinessOffset = readinessOffset;
        ReadinessCount = readinessCount;
        SwapchainImageOrdinal = swapchainImageOrdinal;
    }

    internal MemoryType MemoryType { get; }
    internal int ImportOrdinal { get; set; }
    internal GraphResourceUsage InitialState { get; }
    internal GraphResourceUsage FinalState { get; }
    internal bool ContentsAvailable { get; }
    internal int ReadinessOffset { get; }
    internal int ReadinessCount { get; }
    internal int SwapchainImageOrdinal { get; }
    internal int InitialDataOrdinal { get; set; }
    internal long InitialDataAddress { get; set; }
    internal int InitialDataLength { get; set; }
    internal bool IsImported => ImportOrdinal >= 0;
    internal bool IsSwapchainImage => SwapchainImageOrdinal >= 0;
    internal bool ContentsInitialized =>
        InitialDataOrdinal >= 0 || InitialDataAddress >= 0;
}

internal struct PassData
{
    internal PassData(QueueType queue, PassFlags flags)
    {
        Queue = queue;
        Flags = flags;
        DepthStencilAttachmentOrdinal = -1;
    }

    internal QueueType Queue { get; set; }
    internal PassFlags Flags { get; set; }
    internal int AccessOffset;
    internal int AccessCount;
    internal int ColorAttachmentOffset;
    internal int ColorAttachmentCount;
    internal int DepthStencilAttachmentOrdinal;
    internal int ShaderArgumentOffset;
    internal int ShaderArgumentCount;
    internal int ParameterDescriptorCount;
    internal int ParameterPushConstantOffset;
    internal int ParameterPushConstantCount;
    internal int QueryAccessOffset;
    internal int QueryAccessCount;
    internal int BindlessAccessOffset;
    internal int BindlessAccessCount;
    internal int DependencyOffset;
    internal int DependencyCount;
    internal int BeforeBarrierOffset;
    internal int BeforeBarrierCount;
    internal int AfterBarrierOffset;
    internal int AfterBarrierCount;
    internal int DescriptorOffset;
    internal int DescriptorCount;
    internal int PushConstantOffset;
    internal int PushConstantCount;
    internal int AccessBucketOffset;
    internal int AccessBucketCount;
    internal int BindlessAccessBucketOffset;
    internal int BindlessAccessBucketCount;
    internal int QueryBucketOffset;
    internal int QueryBucketCount;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PassInputData
{
    internal PassInputData(
        int Resource,
        int View,
        GraphAccess Flags,
        GraphResourceUsage State,
        BufferRange BufferRange)
    {
        this = default;
        this.Resource = Resource;
        this.View = View;
        this.Flags = Flags;
        this.State = State;
        this.BufferRange = BufferRange;
    }

    internal PassInputData(
        int Resource,
        int View,
        GraphAccess Flags,
        GraphResourceUsage State,
        TextureSubresourceRange TextureRange)
    {
        this = default;
        this.Resource = ~Resource;
        this.View = View;
        this.Flags = Flags;
        this.State = State;
        this.TextureRange = TextureRange;
    }

    internal readonly bool IsBuffer => Resource >= 0;
    internal readonly int Buffer => IsBuffer
        ? Resource
        : throw new InvalidOperationException("The access targets a texture.");
    internal readonly int Texture => !IsBuffer
        ? ~Resource
        : throw new InvalidOperationException("The access targets a buffer.");

    [FieldOffset(0)] internal GraphAccess Flags;
    [FieldOffset(4)] internal int Resource;
    [FieldOffset(8)] internal int View;
    [FieldOffset(12)] internal GraphResourceUsage State;
    [FieldOffset(16)] internal BufferRange BufferRange;
    [FieldOffset(16)] internal TextureSubresourceRange TextureRange;
}

internal readonly struct PassFragmentData
{
    internal PassFragmentData(
        int slot,
        int view,
        int access,
        LoadType load,
        Vector4 clearColor,
        int resolveView,
        int resolveAccess,
        ResolveType resolveMode)
    {
        Slot = slot;
        View = view;
        Access = access;
        Load = load;
        ClearColor = clearColor;
        ResolveView = resolveView;
        ResolveAccess = resolveAccess;
        ResolveType = resolveMode;
        DepthAccess = -1;
        StencilAccess = -1;
        HasDepth = false;
        DepthLoad = default;
        DepthReadOnly = false;
        ClearDepth = 1f;
        HasStencil = false;
        StencilLoad = default;
        StencilReadOnly = false;
        ClearStencil = 0;
    }

    internal PassFragmentData(
        int view,
        int depthAccess,
        int stencilAccess,
        bool hasDepth,
        LoadType depthLoad,
        bool depthReadOnly,
        float clearDepth,
        bool hasStencil,
        LoadType stencilLoad,
        bool stencilReadOnly,
        byte clearStencil)
    {
        Slot = -1;
        View = view;
        Access = -1;
        Load = default;
        ClearColor = default;
        ResolveView = -1;
        ResolveAccess = -1;
        ResolveType = default;
        DepthAccess = depthAccess;
        StencilAccess = stencilAccess;
        HasDepth = hasDepth;
        DepthLoad = depthLoad;
        DepthReadOnly = depthReadOnly;
        ClearDepth = clearDepth;
        HasStencil = hasStencil;
        StencilLoad = stencilLoad;
        StencilReadOnly = stencilReadOnly;
        ClearStencil = clearStencil;
    }

    internal int Slot { get; }
    internal int View { get; }
    internal int Access { get; }
    internal LoadType Load { get; }
    internal Vector4 ClearColor { get; }
    internal int ResolveView { get; }
    internal int ResolveAccess { get; }
    internal ResolveType ResolveType { get; }
    internal int DepthAccess { get; }
    internal int StencilAccess { get; }
    internal bool HasDepth { get; }
    internal LoadType DepthLoad { get; }
    internal bool DepthReadOnly { get; }
    internal float ClearDepth { get; }
    internal bool HasStencil { get; }
    internal LoadType StencilLoad { get; }
    internal bool StencilReadOnly { get; }
    internal byte ClearStencil { get; }
    internal bool HasResolve => ResolveView >= 0;
}
