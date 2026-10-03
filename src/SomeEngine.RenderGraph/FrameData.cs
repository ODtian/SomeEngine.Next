namespace SomeEngine.RenderGraph;

public delegate void RasterFrameCallback<TState>(
    ref RasterPassCommandScope commands,
    in TState state);

public delegate void ComputeFrameCallback<TState>(
    ref ComputePassCommandScope commands,
    in TState state);

public delegate void CopyFrameCallback<TState>(
    ref CopyPassCommandScope commands,
    in TState state);

public delegate void GeneralFrameCallback<TState>(
    ref GeneralPassCommandScope commands,
    in TState state);

internal abstract class FramePassCallbackStore
{
    internal abstract void Reset();
    internal abstract void Record(int item, RenderGraphFrameState frame, int passIndex, CommandContext context);
}

internal sealed class RasterFramePassCallbackStore<TState> : FramePassCallbackStore
{
    private TState[] _states = new TState[8];
    private RasterFrameCallback<TState>[] _callbacks = new RasterFrameCallback<TState>[8];
    private int _count;

    internal int Add(in TState state, RasterFrameCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureCapacity(_count + 1);
        int item = _count++;
        _states[item] = state;
        _callbacks[item] = callback;
        return item;
    }

    internal override void Record(int item, RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new RasterPassCommandScope(frame, passIndex, context);
        _callbacks[item](ref scope, in _states[item]);
        scope.Finish();
    }

    internal override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>())
            Array.Clear(_states, 0, _count);
        Array.Clear(_callbacks, 0, _count);
        _count = 0;
    }

    private void EnsureCapacity(int value)
    {
        if (value <= _states.Length) return;
        int size = Math.Max(value, _states.Length * 2);
        Array.Resize(ref _states, size);
        Array.Resize(ref _callbacks, size);
    }
}

internal sealed class ComputeFramePassCallbackStore<TState> : FramePassCallbackStore
{
    private TState[] _states = new TState[8];
    private ComputeFrameCallback<TState>[] _callbacks = new ComputeFrameCallback<TState>[8];
    private int _count;
    internal int Add(in TState state, ComputeFrameCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureCapacity(_count + 1);
        int item = _count++;
        _states[item] = state;
        _callbacks[item] = callback;
        return item;
    }
    internal override void Record(int item, RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new ComputePassCommandScope(frame, passIndex, context);
        _callbacks[item](ref scope, in _states[item]);
        scope.Finish();
    }
    internal override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>()) Array.Clear(_states, 0, _count);
        Array.Clear(_callbacks, 0, _count);
        _count = 0;
    }
    private void EnsureCapacity(int value)
    {
        if (value <= _states.Length) return;
        int size = Math.Max(value, _states.Length * 2);
        Array.Resize(ref _states, size);
        Array.Resize(ref _callbacks, size);
    }
}

internal sealed class CopyFramePassCallbackStore<TState> : FramePassCallbackStore
{
    private TState[] _states = new TState[8];
    private CopyFrameCallback<TState>[] _callbacks = new CopyFrameCallback<TState>[8];
    private int _count;
    internal int Add(in TState state, CopyFrameCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureCapacity(_count + 1);
        int item = _count++;
        _states[item] = state;
        _callbacks[item] = callback;
        return item;
    }
    internal override void Record(int item, RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new CopyPassCommandScope(frame, passIndex, context);
        _callbacks[item](ref scope, in _states[item]);
        scope.Finish();
    }
    internal override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>()) Array.Clear(_states, 0, _count);
        Array.Clear(_callbacks, 0, _count);
        _count = 0;
    }
    private void EnsureCapacity(int value)
    {
        if (value <= _states.Length) return;
        int size = Math.Max(value, _states.Length * 2);
        Array.Resize(ref _states, size);
        Array.Resize(ref _callbacks, size);
    }
}

internal sealed class GeneralFramePassCallbackStore<TState> : FramePassCallbackStore
{
    private TState[] _states = new TState[8];
    private GeneralFrameCallback<TState>[] _callbacks = new GeneralFrameCallback<TState>[8];
    private int _count;
    internal int Add(in TState state, GeneralFrameCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureCapacity(_count + 1);
        int item = _count++;
        _states[item] = state;
        _callbacks[item] = callback;
        return item;
    }
    internal override void Record(int item, RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        var scope = new GeneralPassCommandScope(frame, passIndex, context);
        _callbacks[item](ref scope, in _states[item]);
        scope.Finish();
    }
    internal override void Reset()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TState>()) Array.Clear(_states, 0, _count);
        Array.Clear(_callbacks, 0, _count);
        _count = 0;
    }
    private void EnsureCapacity(int value)
    {
        if (value <= _states.Length) return;
        int size = Math.Max(value, _states.Length * 2);
        Array.Resize(ref _states, size);
        Array.Resize(ref _callbacks, size);
    }
}

internal readonly record struct FramePassCallbackStoreKey(GraphPassKind Kind, Type StateType);

internal readonly record struct BufferContents(BufferRange Range, ResourceContentState Contents, BufferContentScope Scope);
internal readonly record struct TextureContents(TextureSubresourceRange Range, ResourceContentState Contents);
internal readonly record struct BufferSyncState(BufferRange Range, PipelineSync Sync, ResourceAccess Access,
    Queue? Queue, Queue? ReleasedTo);
internal readonly record struct TextureSyncState(TextureSubresourceRange Range, PipelineSync Sync, ResourceAccess Access,
    TextureLayout Layout, Queue? Queue, Queue? ReleasedTo, TextureSubresourceRange? ReleaseRange);
internal readonly record struct BufferSyncInput(Buffer? Resource, Resource? Alias, BufferSyncState[]? States);
internal readonly record struct TextureSyncInput(Texture? Resource, Resource? Alias, TextureSyncState[]? States);

internal struct FrameBuffer
{
    internal GraphIdentity Identity;
    internal GraphIdentity AllocationIdentity;
    internal GraphBuffer? Definition;
    internal BufferDesc Description;
    internal MemoryType MemoryType;
    internal RenderGraphResourceOwnership Ownership;
    internal RenderGraphResourceLifetime Lifetime;
    internal MemoryRequirements Requirements;
    internal Buffer? Resource;
    internal BufferBoundaryState[]? EntryBoundaryStates;
    internal FrameTransientResourceAllocator.UploadPage? UploadData;
    internal BufferContents[]? Contents;
    internal BufferSyncInput Synchronization;
    internal int FirstPass;
    internal int LastPass;
    internal ResourcePlacement Placement;
}

internal struct FrameTexture
{
    internal GraphIdentity Identity;
    internal GraphIdentity AllocationIdentity;
    internal GraphTexture? Definition;
    internal TextureDimension Dimension;
    internal uint Width;
    internal uint Height;
    internal uint Depth;
    internal uint MipLevelCount;
    internal uint ArrayLayerCount;
    internal uint SampleCount;
    internal Format Format;
    internal TextureUsages Usages;
    internal Format[]? PermittedViewFormats;
    internal string? Label;
    internal ResourceNodePlacement NodePlacement;
    internal RenderGraphResourceOwnership Ownership;
    internal RenderGraphResourceLifetime Lifetime;
    internal MemoryRequirements Requirements;
    internal Texture? Resource;
    internal TextureBoundaryState[]? EntryBoundaryStates;
    internal TextureContents[]? Contents;
    internal TextureSyncInput Synchronization;
    internal int FirstPass;
    internal int LastPass;
    internal ResourcePlacement Placement;

    internal TextureDesc BorrowDescription() => new(
        Dimension, Width, Height, Depth, MipLevelCount, ArrayLayerCount, SampleCount,
        Format, Usages, PermittedViewFormats, Label, NodePlacement);
}

internal struct FrameQueryPool
{
    internal GraphIdentity Identity;
    internal GraphQueryPool Definition;
    internal QueryPool Resource;
    internal QueryBoundaryState[] EntryBoundaryStates;
    internal (QueryRange Range, ResourceContentState Contents)[]? Contents;
}

internal struct FrameRayTracingShaderTable
{
    internal GraphIdentity Identity;
    internal GraphRayTracingShaderTable Definition;
    internal RayTracingShaderTable Resource;
    internal GraphParameterResourceBinding[] Inventory;
    internal RayTracingShaderTableBoundaryState[] EntryBoundaryStates;
    internal ResourceContentState Contents;
}

internal struct FrameView
{
    internal FrameView(GraphView source)
    {
        Kind = source.Kind;
        Buffer = source.Buffer;
        BufferInput = source.BufferInput;
        TextureInput = source.TextureInput;
        Texture = source.Texture;
        AdditionalBuffer = source.AdditionalBuffer;
        BufferRange = source.BufferRange;
        TextureRange = source.TextureRange;
        BufferFormat = source.BufferFormat;
        TextureFormat = source.TextureFormat;
        StructureStride = source.StructureStride;
        CounterOffset = source.CounterOffset;
        Dimension = source.Dimension;
        ReadOnlyDepth = source.ReadOnlyDepth;
        ReadOnlyStencil = source.ReadOnlyStencil;
        Label = source.Label;
    }

    internal GraphIdentity Identity;
    internal GraphView? Definition;
    internal int BufferIndex;
    internal int TextureIndex;
    internal int AdditionalBufferIndex;
    internal bool Connected;
    internal GraphViewKind Kind;
    internal GraphIdentity Buffer;
    internal GraphBufferInput? BufferInput;
    internal GraphTextureInput? TextureInput;
    internal GraphIdentity Texture;
    internal GraphIdentity AdditionalBuffer;
    internal BufferRange BufferRange;
    internal TextureSubresourceRange TextureRange;
    internal Format? BufferFormat;
    internal Format TextureFormat;
    internal uint StructureStride;
    internal ulong CounterOffset;
    internal TextureViewDimension Dimension;
    internal bool ReadOnlyDepth;
    internal bool ReadOnlyStencil;
    internal string? Label;
    internal DeviceResource? View;
}

internal struct FramePass
{
    internal GraphIdentity Identity;
    internal GraphPass? Definition;
    internal string Label;
    internal GraphPassKind Kind;
    internal PassQueueSelection QueuePolicy;
    internal PassOptions Options;
    internal Pipeline? Pipeline;
    internal VariableLayoutReflection ParameterLayout;
    internal byte[]? ParameterOrdinaryData;
    internal List<GraphParameterResourceBinding>? ParameterBindings;
    internal List<GraphIdentity>? PersistentBindings;
    internal long DeclarationOrdinal;
    internal bool IsBufferUpload;
    internal bool Enabled;
    internal bool Live;
    internal Queue? Queue;
    internal int FirstAccess;
    internal int AccessCount;
    internal PassCallbackStorage? PersistentCallbacks;
    internal FramePassCallbackStore? FrameCallbacks;
    internal int FrameCallbackIndex;
    internal int ExtensionPointIndex;
}

internal struct FrameResourceAccess
{
    internal GraphIdentity Identity;
    internal GraphIdentity Pass;
    internal GraphAccessTargetKind TargetKind;
    internal GraphIdentity Target;
    internal GraphAccessMode Mode;
    internal WriteCoverage Coverage;
    internal PipelineSync Sync;
    internal ResourceAccess Access;
    internal BufferRange BufferRange;
    internal BufferRangeSet? SharedBufferRanges;
    internal BufferAccessRanges BufferRanges => new(BufferRange, SharedBufferRanges);
    internal TextureSubresourceRange TextureRange;
    internal QueryRange QueryRange;
    internal TextureLayout TextureLayout;
    internal ResourceContentState? ResultContents;
    internal BufferContentScope BufferContentScope;
    internal UnorderedAccessHazardMode UnorderedAccessHazards;
    internal int ResourceIndex;
    internal int PassIndex;
    internal int PassAdjacencyIndex;
    internal int ResourceAdjacencyIndex;
    internal bool Connected;
}

internal readonly struct BufferAccessRanges(BufferRange range, BufferRangeSet? ranges)
{
    internal int Count => ranges?.Count ?? 1;
    public Enumerator GetEnumerator() => new(range, ranges);

    internal struct Enumerator(BufferRange range, BufferRangeSet? ranges)
    {
        private int _index = -1;
        public bool MoveNext() => ++_index < (ranges?.Count ?? 1);
        public BufferRange Current => ranges is null ? range :
            ranges.Ranges[_index].IsWhole ? range : ranges.Ranges[_index];
    }
}

internal readonly record struct ParameterInputValue(
    ResourceBinding Binding,
    GraphIdentity Resource,
    BufferRange BufferRange,
    TextureSubresourceRange TextureRange,
    GraphIdentity Secondary,
    BufferRange SecondaryBufferRange,
    TextureSubresourceRange SecondaryTextureRange);
