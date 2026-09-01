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

    private sealed class Storage
    {
        internal readonly List<Slot> Slots = [];
        internal readonly List<T> Rows = [];
        internal readonly List<int> RowSlots = [];
        internal int FirstFree = -1;
    }

    private readonly Func<T, T>? _clone;
    private readonly GraphIdentityDomain _domain;
    private Storage _storage;
    private bool _writable;

    internal SlotMap(GraphIdentityDomain domain, Func<T, T>? clone = null)
    {
        _domain = domain;
        _clone = clone;
        _storage = new Storage();
        _writable = true;
    }

    private SlotMap(GraphIdentityDomain domain, Func<T, T>? clone, Storage storage)
    {
        _domain = domain;
        _clone = clone;
        _storage = storage;
    }

    internal int Count => _storage.Rows.Count;
    internal List<T> Rows => _storage.Rows;
    internal object BlockIdentity => _storage;

    internal SlotMap<T> Fork()
    {
        _writable = false;
        return new SlotMap<T>(_domain, _clone, _storage);
    }

    internal GraphIdentity Add(ulong owner, T value)
    {
        EnsureWritable();
        List<Slot> slots = _storage.Slots;
        List<T> rows = _storage.Rows;
        List<int> rowSlots = _storage.RowSlots;
        int slot;
        uint generation;
        if (_storage.FirstFree >= 0)
        {
            slot = _storage.FirstFree;
            Slot reused = slots[slot];
            _storage.FirstFree = reused.NextFree;
            generation = reused.Generation;
            reused.DenseIndex = rows.Count;
            reused.NextFree = -1;
            slots[slot] = reused;
        }
        else
        {
            slot = slots.Count;
            generation = 1;
            slots.Add(new Slot
            {
                Generation = generation,
                DenseIndex = rows.Count,
                NextFree = -1,
            });
        }

        rows.Add(value);
        rowSlots.Add(slot);
        return new GraphIdentity(owner, slot, generation, _domain);
    }

    internal bool Contains(in GraphIdentity id)
    {
        if (!id.IsValid || (uint)id.Slot >= (uint)_storage.Slots.Count)
            return false;
        Slot slot = _storage.Slots[id.Slot];
        return !slot.Retired && slot.DenseIndex >= 0 && slot.Generation == id.Generation;
    }

    internal int DenseIndex(in GraphIdentity id)
    {
        if (!Contains(id))
            throw new ArgumentException("The graph identity is invalid, stale, or belongs to another owner.");
        return _storage.Slots[id.Slot].DenseIndex;
    }

    internal T Get(in GraphIdentity id) => _storage.Rows[DenseIndex(id)];

    internal T GetMutable(in GraphIdentity id)
    {
        EnsureWritable();
        return _storage.Rows[DenseIndex(id)];
    }

    internal void Set(in GraphIdentity id, T value)
    {
        EnsureWritable();
        _storage.Rows[DenseIndex(id)] = value;
    }

    internal T Remove(in GraphIdentity id)
    {
        EnsureWritable();
        List<Slot> slots = _storage.Slots;
        List<T> rows = _storage.Rows;
        List<int> rowSlots = _storage.RowSlots;
        int dense = DenseIndex(id);
        T removed = rows[dense];
        int last = rows.Count - 1;
        int slotIndex = id.Slot;
        if (dense != last)
        {
            rows[dense] = rows[last];
            int movedSlot = rowSlots[last];
            rowSlots[dense] = movedSlot;
            Slot moved = slots[movedSlot];
            moved.DenseIndex = dense;
            slots[movedSlot] = moved;
        }
        rows.RemoveAt(last);
        rowSlots.RemoveAt(last);

        Slot slot = slots[slotIndex];
        slot.DenseIndex = -1;
        if (slot.Generation == uint.MaxValue)
        {
            slot.Retired = true;
            slot.NextFree = -1;
        }
        else
        {
            slot.Generation++;
            slot.NextFree = _storage.FirstFree;
            _storage.FirstFree = slotIndex;
        }
        slots[slotIndex] = slot;
        return removed;
    }

    internal GraphIdentity IdentityAt(ulong owner, int denseIndex)
    {
        int slotIndex = _storage.RowSlots[denseIndex];
        Slot slot = _storage.Slots[slotIndex];
        return new GraphIdentity(owner, slotIndex, slot.Generation, _domain);
    }

    private void EnsureWritable()
    {
        if (_writable) return;
        Storage source = _storage;
        var copy = new Storage { FirstFree = source.FirstFree };
        copy.Slots.AddRange(source.Slots);
        copy.RowSlots.AddRange(source.RowSlots);
        if (_clone is null)
            copy.Rows.AddRange(source.Rows);
        else
            foreach (T row in source.Rows) copy.Rows.Add(_clone(row));
        _storage = copy;
        _writable = true;
    }
}

internal sealed class SharedList<T> : IEnumerable<T>
{
    private sealed class Storage
    {
        internal readonly List<T> Values = [];
    }

    private Storage _storage;
    private bool _writable;

    internal SharedList()
    {
        _storage = new Storage();
        _writable = true;
    }

    private SharedList(Storage storage) => _storage = storage;

    internal int Count => _storage.Values.Count;
    internal object BlockIdentity => _storage;

    internal SharedList<T> Fork()
    {
        _writable = false;
        return new SharedList<T>(_storage);
    }

    internal bool Contains(T value) => _storage.Values.Contains(value);

    internal void Add(T value)
    {
        EnsureWritable();
        _storage.Values.Add(value);
    }

    internal int RemoveAll(Predicate<T> predicate)
    {
        EnsureWritable();
        return _storage.Values.RemoveAll(predicate);
    }

    internal void Clear()
    {
        EnsureWritable();
        _storage.Values.Clear();
    }

    public List<T>.Enumerator GetEnumerator() => _storage.Values.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureWritable()
    {
        if (_writable) return;
        var copy = new Storage();
        copy.Values.AddRange(_storage.Values);
        _storage = copy;
        _writable = true;
    }
}

internal sealed class GraphStructure
{
    internal SlotMap<GraphBuffer> Buffers { get; private init; } =
        new(GraphIdentityDomain.Buffer, static value => value.Clone());
    internal SlotMap<GraphTexture> Textures { get; private init; } =
        new(GraphIdentityDomain.Texture, static value => value.Clone());
    internal SlotMap<GraphPersistentParameterBindings> PersistentBindings { get; private init; } =
        new(GraphIdentityDomain.PersistentBindings, static value => value.Clone());
    internal SlotMap<GraphQueryPool> QueryPools { get; private init; } =
        new(GraphIdentityDomain.QueryPool, static value => value.Clone());
    internal SlotMap<GraphRayTracingShaderTable> ShaderTables { get; private init; } =
        new(GraphIdentityDomain.ShaderTable, static value => value.Clone());
    internal SlotMap<GraphView> Views { get; private init; } =
        new(GraphIdentityDomain.View, static value => value.Clone());
    internal SlotMap<GraphPass> Passes { get; private init; } =
        new(GraphIdentityDomain.Pass, static value => value.Clone());
    internal SlotMap<PassResourceAccess> Accesses { get; private init; } =
        new(GraphIdentityDomain.Access, static value => value.Clone());
    internal SlotMap<GraphExtensionPoint> ExtensionPoints { get; private init; } =
        new(GraphIdentityDomain.ExtensionPoint);
    internal SharedList<ExplicitPassOrder> Orders { get; private init; } = new();
    internal int NextDeclarationOrdinal { get; set; }

    internal GraphStructure Fork()
    {
        return new GraphStructure
        {
            Buffers = Buffers.Fork(),
            Textures = Textures.Fork(),
            PersistentBindings = PersistentBindings.Fork(),
            QueryPools = QueryPools.Fork(),
            ShaderTables = ShaderTables.Fork(),
            Views = Views.Fork(),
            Passes = Passes.Fork(),
            Accesses = Accesses.Fork(),
            ExtensionPoints = ExtensionPoints.Fork(),
            Orders = Orders.Fork(),
            NextDeclarationOrdinal = NextDeclarationOrdinal,
        };
    }

    internal int CountSharedBlocks(GraphStructure other)
    {
        int count = 0;
        if (ReferenceEquals(Buffers.BlockIdentity, other.Buffers.BlockIdentity)) count++;
        if (ReferenceEquals(Textures.BlockIdentity, other.Textures.BlockIdentity)) count++;
        if (ReferenceEquals(PersistentBindings.BlockIdentity, other.PersistentBindings.BlockIdentity)) count++;
        if (ReferenceEquals(QueryPools.BlockIdentity, other.QueryPools.BlockIdentity)) count++;
        if (ReferenceEquals(ShaderTables.BlockIdentity, other.ShaderTables.BlockIdentity)) count++;
        if (ReferenceEquals(Views.BlockIdentity, other.Views.BlockIdentity)) count++;
        if (ReferenceEquals(Passes.BlockIdentity, other.Passes.BlockIdentity)) count++;
        if (ReferenceEquals(Accesses.BlockIdentity, other.Accesses.BlockIdentity)) count++;
        if (ReferenceEquals(ExtensionPoints.BlockIdentity, other.ExtensionPoints.BlockIdentity)) count++;
        if (ReferenceEquals(Orders.BlockIdentity, other.Orders.BlockIdentity)) count++;
        return count;
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
    internal ShaderVariableLayout ParameterLayout = ShaderVariableLayout.Null;
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

