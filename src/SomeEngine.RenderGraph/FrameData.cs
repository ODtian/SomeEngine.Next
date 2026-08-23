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

internal struct FrameBuffer
{
    internal GraphIdentity Identity;
    internal GraphBuffer? Definition;
    internal BufferDesc Description;
    internal MemoryType MemoryType;
    internal RenderGraphResourceOwnership Ownership;
    internal RenderGraphResourceLifetime Lifetime;
    internal MemoryRequirements Requirements;
    internal Buffer? Resource;
    internal BufferBoundaryState[]? EntryBoundaryStates;
    internal byte[]? InitialData;
    internal int FirstUse;
    internal int LastUse;
    internal ResourcePlacement Placement;
}

internal struct FrameTexture
{
    internal GraphIdentity Identity;
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
    internal int FirstUse;
    internal int LastUse;
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
}

internal struct FrameRayTracingShaderTable
{
    internal GraphIdentity Identity;
    internal GraphRayTracingShaderTable Definition;
    internal RayTracingShaderTable Resource;
    internal GraphParameterResourceBinding[] Inventory;
    internal RayTracingShaderTableBoundaryState[] EntryBoundaryStates;
}

internal struct FrameView
{
    internal GraphIdentity Identity;
    internal GraphView? Definition;
    internal GraphViewKind Kind;
    internal GraphIdentity Buffer;
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
    internal int DeclarationOrdinal;
    internal bool Enabled;
    internal bool Live;
    internal Queue? Queue;
    internal int ScheduledOrdinal;
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
    internal TextureSubresourceRange TextureRange;
    internal QueryRange QueryRange;
    internal TextureLayout TextureLayout;
    internal ResourceContentState? ResultContents;
    internal int ResourceIndex;
    internal int PassIndex;
}

