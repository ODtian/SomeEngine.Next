namespace SomeEngine.RenderGraph;

internal sealed class SlotMap<T>
{
    private struct Slot
    {
        internal uint Generation;
        internal int DenseIndex;
        internal int NextFree;
        internal bool Retired;
    }

    private readonly List<Slot> _slots = [];
    private readonly List<T> _rows = [];
    private readonly List<int> _rowSlots = [];
    private int _firstFree = -1;

    internal int Count => _rows.Count;
    internal List<T> Rows => _rows;

    internal GraphIdentity Add(ulong owner, T value)
    {
        int slot;
        uint generation;
        if (_firstFree >= 0)
        {
            slot = _firstFree;
            Slot reused = _slots[slot];
            _firstFree = reused.NextFree;
            generation = reused.Generation;
            reused.DenseIndex = _rows.Count;
            reused.NextFree = -1;
            _slots[slot] = reused;
        }
        else
        {
            slot = _slots.Count;
            generation = 1;
            _slots.Add(new Slot
            {
                Generation = generation,
                DenseIndex = _rows.Count,
                NextFree = -1,
            });
        }

        _rows.Add(value);
        _rowSlots.Add(slot);
        return new GraphIdentity(owner, slot, generation);
    }

    internal bool Contains(in GraphIdentity id)
    {
        if (!id.IsValid || (uint)id.Slot >= (uint)_slots.Count)
            return false;
        Slot slot = _slots[id.Slot];
        return !slot.Retired && slot.DenseIndex >= 0 && slot.Generation == id.Generation;
    }

    internal int DenseIndex(in GraphIdentity id)
    {
        if (!Contains(id))
            throw new ArgumentException("The graph identity is invalid, stale, or belongs to another owner.");
        return _slots[id.Slot].DenseIndex;
    }

    internal T Get(in GraphIdentity id) => _rows[DenseIndex(id)];

    internal void Set(in GraphIdentity id, T value) => _rows[DenseIndex(id)] = value;

    internal T Remove(in GraphIdentity id)
    {
        int dense = DenseIndex(id);
        T removed = _rows[dense];
        int last = _rows.Count - 1;
        int slotIndex = id.Slot;
        if (dense != last)
        {
            _rows[dense] = _rows[last];
            int movedSlot = _rowSlots[last];
            _rowSlots[dense] = movedSlot;
            Slot moved = _slots[movedSlot];
            moved.DenseIndex = dense;
            _slots[movedSlot] = moved;
        }
        _rows.RemoveAt(last);
        _rowSlots.RemoveAt(last);

        Slot slot = _slots[slotIndex];
        slot.DenseIndex = -1;
        if (slot.Generation == uint.MaxValue)
        {
            slot.Retired = true;
            slot.NextFree = -1;
        }
        else
        {
            slot.Generation++;
            slot.NextFree = _firstFree;
            _firstFree = slotIndex;
        }
        _slots[slotIndex] = slot;
        return removed;
    }

    internal GraphIdentity IdentityAt(ulong owner, int denseIndex)
    {
        int slotIndex = _rowSlots[denseIndex];
        Slot slot = _slots[slotIndex];
        return new GraphIdentity(owner, slotIndex, slot.Generation);
    }

    internal SlotMap<T> Clone(Func<T, T>? clone = null)
    {
        var result = new SlotMap<T>();
        result._slots.AddRange(_slots);
        result._rowSlots.AddRange(_rowSlots);
        if (clone is null)
            result._rows.AddRange(_rows);
        else
            foreach (T row in _rows) result._rows.Add(clone(row));
        result._firstFree = _firstFree;
        return result;
    }
}

internal sealed class GraphStructure
{
    internal SlotMap<GraphBuffer> Buffers { get; private set; } = new();
    internal SlotMap<GraphTexture> Textures { get; private set; } = new();
    internal SlotMap<GraphPersistentParameterBindings> PersistentBindings { get; private set; } = new();
    internal SlotMap<GraphQueryPool> QueryPools { get; private set; } = new();
    internal SlotMap<GraphRayTracingShaderTable> ShaderTables { get; private set; } = new();
    internal SlotMap<GraphView> Views { get; private set; } = new();
    internal SlotMap<GraphPass> Passes { get; private set; } = new();
    internal SlotMap<PassResourceAccess> Accesses { get; private set; } = new();
    internal SlotMap<GraphExtensionPoint> ExtensionPoints { get; private set; } = new();
    internal List<ExplicitPassOrder> Orders { get; private set; } = [];

    internal GraphStructure Clone()
    {
        return new GraphStructure
        {
            Buffers = Buffers.Clone(static value => value.Clone()),
            Textures = Textures.Clone(static value => value.Clone()),
            PersistentBindings = PersistentBindings.Clone(static value => value.Clone()),
            QueryPools = QueryPools.Clone(static value => value.Clone()),
            ShaderTables = ShaderTables.Clone(static value => value.Clone()),
            Views = Views.Clone(static value => value.Clone()),
            Passes = Passes.Clone(static value => value.Clone()),
            Accesses = Accesses.Clone(static value => value.Clone()),
            ExtensionPoints = ExtensionPoints.Clone(static value => value with { }),
            Orders = new List<ExplicitPassOrder>(Orders),
        };
    }
}

internal sealed class GraphBuffer
{
    internal required BufferDesc Description;
    internal required MemoryType MemoryType;
    internal required RenderGraphResourceOwnership Ownership;
    internal required RenderGraphResourceLifetime Lifetime;
    internal MemoryRequirements Requirements;
    internal Buffer? PersistentResource;
    internal Buffer? RegisteredResource;
    internal BufferBoundaryState[] BoundaryStates = [];
    internal string? Label => Description.Label;

    internal GraphBuffer Clone() => new()
    {
        Description = Description,
        MemoryType = MemoryType,
        Ownership = Ownership,
        Lifetime = Lifetime,
        Requirements = Requirements,
        PersistentResource = PersistentResource,
        RegisteredResource = RegisteredResource,
        BoundaryStates = (BufferBoundaryState[])BoundaryStates.Clone(),
    };
}

internal sealed class GraphTexture
{
    internal required TextureDimension Dimension;
    internal required uint Width;
    internal required uint Height;
    internal required uint Depth;
    internal required uint MipLevelCount;
    internal required uint ArrayLayerCount;
    internal required uint SampleCount;
    internal required Format Format;
    internal required TextureUsages Usages;
    internal required Format[] PermittedViewFormats;
    internal required string? Label;
    internal required ResourceNodePlacement NodePlacement;
    internal required RenderGraphResourceOwnership Ownership;
    internal required RenderGraphResourceLifetime Lifetime;
    internal MemoryRequirements Requirements;
    internal Texture? PersistentResource;
    internal Texture? RegisteredResource;
    internal TextureBoundaryState[] BoundaryStates = [];

    internal GraphTexture Clone() => new()
    {
        Dimension = Dimension,
        Width = Width,
        Height = Height,
        Depth = Depth,
        MipLevelCount = MipLevelCount,
        ArrayLayerCount = ArrayLayerCount,
        SampleCount = SampleCount,
        Format = Format,
        Usages = Usages,
        PermittedViewFormats = (Format[])PermittedViewFormats.Clone(),
        Label = Label,
        NodePlacement = NodePlacement,
        Ownership = Ownership,
        Lifetime = Lifetime,
        Requirements = Requirements,
        PersistentResource = PersistentResource,
        RegisteredResource = RegisteredResource,
        BoundaryStates = (TextureBoundaryState[])BoundaryStates.Clone(),
    };

    internal TextureDesc BorrowDescription() => new(
        Dimension,
        Width,
        Height,
        Depth,
        MipLevelCount,
        ArrayLayerCount,
        SampleCount,
        Format,
        Usages,
        PermittedViewFormats,
        Label,
        NodePlacement);
}

internal sealed class GraphPersistentParameterBindings
{
    internal required PersistentParameterBindings Resource;
    internal required GraphParameterResourceBinding[] Inventory;

    internal GraphPersistentParameterBindings Clone() => new()
    {
        Resource = Resource,
        Inventory = (GraphParameterResourceBinding[])Inventory.Clone(),
    };
}

internal readonly record struct QueryBoundaryState(
    QueryRange Range,
    ResourceContentState Contents,
    Queue? Queue,
    QueueCompletion? ReadyAfter);

internal readonly record struct RayTracingShaderTableBoundaryState(
    ResourceContentState Contents,
    Queue? Queue,
    QueueCompletion? ReadyAfter);

internal sealed class GraphQueryPool
{
    internal required QueryPool Resource;
    internal QueryBoundaryState[] BoundaryStates = [];

    internal GraphQueryPool Clone() => new()
    {
        Resource = Resource,
        BoundaryStates = (QueryBoundaryState[])BoundaryStates.Clone(),
    };
}

internal sealed class GraphRayTracingShaderTable
{
    internal required RayTracingShaderTable Resource;
    internal required GraphParameterResourceBinding[] Inventory;
    internal RayTracingShaderTableBoundaryState[] BoundaryStates = [];

    internal GraphRayTracingShaderTable Clone() => new()
    {
        Resource = Resource,
        Inventory = (GraphParameterResourceBinding[])Inventory.Clone(),
        BoundaryStates = (RayTracingShaderTableBoundaryState[])BoundaryStates.Clone(),
    };
}

internal enum GraphViewKind : byte
{
    BufferCbv,
    BufferSrv,
    BufferUav,
    TextureSrv,
    TextureUav,
    ColorAttachment,
    DepthStencil,
}

internal sealed class GraphView
{
    internal required GraphViewKind Kind;
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
    internal DeviceResource? PersistentView;

    internal GraphView Clone() => (GraphView)MemberwiseClone();
}

internal readonly record struct ExplicitPassOrder(GraphIdentity Predecessor, GraphIdentity Consumer);

internal readonly record struct GraphExtensionPoint(string Label, int DeclarationOrdinal);

internal sealed class GraphPass
{
    internal required string Label;
    internal required GraphPassKind Kind;
    internal required PassQueueSelection Queue;
    internal required PassOptions Options;
    internal required PassCallbackStorage CallbackStorage;
    internal Pipeline? Pipeline;
    internal VariableLayoutReflection ParameterLayout;
    internal byte[] ParameterOrdinaryData = [];
    internal List<GraphParameterResourceBinding> ParameterBindings = [];
    internal List<GraphIdentity> PersistentBindings = [];
    internal int DeclarationOrdinal;
    internal List<GraphIdentity> Accesses = [];
    internal List<GraphColorAttachment> ColorAttachments = [];
    internal GraphDepthStencilAttachment? DepthStencilAttachment;
    internal List<PassRenderingRegion> RenderingRegions = [];

    internal GraphPass Clone() => new()
    {
        Label = Label,
        Kind = Kind,
        Queue = Queue,
        Options = Options,
        CallbackStorage = CallbackStorage,
        Pipeline = Pipeline,
        ParameterLayout = ParameterLayout,
        ParameterOrdinaryData = (byte[])ParameterOrdinaryData.Clone(),
        ParameterBindings = new List<GraphParameterResourceBinding>(ParameterBindings),
        PersistentBindings = new List<GraphIdentity>(PersistentBindings),
        DeclarationOrdinal = DeclarationOrdinal,
        Accesses = new List<GraphIdentity>(Accesses),
        ColorAttachments = new List<GraphColorAttachment>(ColorAttachments),
        DepthStencilAttachment = DepthStencilAttachment,
        RenderingRegions = new List<PassRenderingRegion>(RenderingRegions),
    };
}

internal sealed class PassResourceAccess
{
    internal required GraphIdentity Pass;
    internal required GraphAccessTargetKind TargetKind;
    internal required GraphIdentity Target;
    internal required GraphAccessMode Mode;
    internal required WriteCoverage Coverage;
    internal required PipelineSync Sync;
    internal required ResourceAccess Access;
    internal BufferRange BufferRange;
    internal TextureSubresourceRange TextureRange;
    internal QueryRange QueryRange;
    internal TextureLayout TextureLayout;
    internal bool DynamicRange;
    internal ResourceContentState? ResultContents;

    internal PassResourceAccess Clone() => (PassResourceAccess)MemberwiseClone();
}

internal readonly record struct GraphColorAttachment(
    uint Slot,
    GraphIdentity View,
    LoadType Load,
    StoreType Store,
    WriteCoverage Coverage,
    Vector4 ClearValue,
    GraphIdentity ResolveView,
    ResolveType ResolveType,
    int RenderingRegionIndex);

internal readonly record struct GraphDepthStencilAttachment(
    GraphIdentity View,
    LoadType DepthLoad,
    StoreType DepthStore,
    WriteCoverage DepthCoverage,
    float ClearDepth,
    LoadType StencilLoad,
    StoreType StencilStore,
    WriteCoverage StencilCoverage,
    byte ClearStencil,
    int RenderingRegionIndex);

internal readonly record struct PassRenderingRegion(
    uint X,
    uint Y,
    uint Width,
    uint Height,
    uint FirstArrayLayer,
    uint ArrayLayerCount);

