namespace SomeEngine.RenderGraph;

internal sealed class SlotMap<T>
{
    private readonly record struct Entry(uint Generation, int DenseIndex, int NextFree, bool Retired = false);
    private readonly Func<T, T>? _clone;
    private readonly GraphIdentityDomain _domain;
    private ImmutableList<Entry> _entries = ImmutableList<Entry>.Empty;
    private ImmutableList<T> _rows = ImmutableList<T>.Empty;
    private ImmutableList<GraphIdentity> _identities = ImmutableList<GraphIdentity>.Empty;
    private HashSet<int> _writableRows = [];
    private int _firstFree = -1;

    internal SlotMap(GraphIdentityDomain domain, Func<T, T>? clone = null)
    {
        _domain = domain;
        _clone = clone;
    }

    internal int Count => _rows.Count;
    internal ImmutableList<T> Rows => _rows;
    internal ImmutableList<GraphIdentity> Identities => _identities;
    internal object BlockIdentity => _rows;

    internal SlotMap<T> Fork()
    {
        // Immutable collection paths are shared; mutable row values are copied only
        // when that particular row is edited. Submitted frames keep their old roots.
        _writableRows = [];
        return new SlotMap<T>(_domain, _clone)
        {
            _entries = _entries,
            _rows = _rows,
            _identities = _identities,
            _firstFree = _firstFree,
        };
    }

    internal GraphIdentity Add(ulong owner, T value)
    {
        int index;
        uint generation;
        if (_firstFree >= 0)
        {
            index = _firstFree;
            Entry entry = _entries[index];
            _firstFree = entry.NextFree;
            generation = entry.Generation;
            _entries = _entries.SetItem(index, entry with { DenseIndex = Count, NextFree = -1 });
        }
        else
        {
            index = _entries.Count;
            generation = 1;
            _entries = _entries.Add(new Entry(generation, Count, -1));
        }
        GraphIdentity identity = new(owner, index, generation, _domain);
        _rows = _rows.Add(value);
        _identities = _identities.Add(identity);
        _writableRows.Add(index);
        return identity;
    }

    internal bool Contains(in GraphIdentity id)
    {
        if (!id.IsValid || id.Domain != _domain || (uint)id.Slot >= (uint)_entries.Count)
            return false;
        Entry entry = _entries[id.Slot];
        return !entry.Retired && entry.DenseIndex >= 0 && entry.Generation == id.Generation &&
            _identities[entry.DenseIndex].Owner == id.Owner;
    }

    internal int DenseIndex(in GraphIdentity id)
    {
        DevelopmentCheck.Require(Contains(id), "The graph identity must belong to this graph.");
        return _entries[id.Slot].DenseIndex;
    }

    internal T Get(in GraphIdentity id) => _rows[DenseIndex(id)];

    internal T GetMutable(in GraphIdentity id)
    {
        int index = DenseIndex(id);
        if (_clone is not null && _writableRows.Add(id.Slot))
            _rows = _rows.SetItem(index, _clone(_rows[index]));
        return _rows[index];
    }

    internal void Set(in GraphIdentity id, T value)
    {
        _rows = _rows.SetItem(DenseIndex(id), value);
        _writableRows.Add(id.Slot);
    }

    internal T Remove(in GraphIdentity id)
    {
        if (!Contains(id))
            throw new ArgumentException("The graph identity is invalid or stale.", nameof(id));
        int dense = DenseIndex(id);
        int last = Count - 1;
        T removed = _rows[dense];
        if (dense != last)
        {
            GraphIdentity moved = _identities[last];
            _rows = _rows.SetItem(dense, _rows[last]);
            _identities = _identities.SetItem(dense, moved);
            _entries = _entries.SetItem(moved.Slot, _entries[moved.Slot] with { DenseIndex = dense });
        }
        _rows = _rows.RemoveAt(last);
        _identities = _identities.RemoveAt(last);
        Entry entry = _entries[id.Slot];
        bool retired = entry.Generation == uint.MaxValue;
        _entries = _entries.SetItem(id.Slot, new Entry(
            retired ? entry.Generation : entry.Generation + 1, -1, retired ? -1 : _firstFree, retired));
        if (!retired) _firstFree = id.Slot;
        _writableRows.Remove(id.Slot);
        return removed;
    }

    internal GraphIdentity IdentityAt(ulong owner, int denseIndex) => _identities[denseIndex];
}

internal sealed class GraphOrders : IEnumerable<ExplicitPassOrder>
{
    private ImmutableHashSet<ExplicitPassOrder> _edges = ImmutableHashSet<ExplicitPassOrder>.Empty;
    private ImmutableDictionary<GraphIdentity, ImmutableHashSet<GraphIdentity>> _predecessors =
        ImmutableDictionary<GraphIdentity, ImmutableHashSet<GraphIdentity>>.Empty;
    private ImmutableDictionary<GraphIdentity, ImmutableHashSet<GraphIdentity>> _successors =
        ImmutableDictionary<GraphIdentity, ImmutableHashSet<GraphIdentity>>.Empty;

    internal int Count => _edges.Count;
    internal object BlockIdentity => _edges;
    internal GraphOrders Fork() => new() { _edges = _edges, _predecessors = _predecessors, _successors = _successors };
    internal bool Contains(ExplicitPassOrder order) => _edges.Contains(order);

    internal void Add(ExplicitPassOrder order)
    {
        if (_edges.Contains(order)) return;
        _edges = _edges.Add(order);
        _predecessors = _predecessors.SetItem(order.Consumer,
            _predecessors.GetValueOrDefault(order.Consumer, ImmutableHashSet<GraphIdentity>.Empty).Add(order.Predecessor));
        _successors = _successors.SetItem(order.Predecessor,
            _successors.GetValueOrDefault(order.Predecessor, ImmutableHashSet<GraphIdentity>.Empty).Add(order.Consumer));
    }

    internal bool Remove(ExplicitPassOrder order)
    {
        if (!_edges.Contains(order)) return false;
        _edges = _edges.Remove(order);
        ImmutableHashSet<GraphIdentity> predecessors = _predecessors[order.Consumer].Remove(order.Predecessor);
        ImmutableHashSet<GraphIdentity> successors = _successors[order.Predecessor].Remove(order.Consumer);
        _predecessors = predecessors.IsEmpty ? _predecessors.Remove(order.Consumer) : _predecessors.SetItem(order.Consumer, predecessors);
        _successors = successors.IsEmpty ? _successors.Remove(order.Predecessor) : _successors.SetItem(order.Predecessor, successors);
        return true;
    }

    internal IEnumerable<ExplicitPassOrder> Incident(GraphIdentity pass)
    {
        foreach (GraphIdentity predecessor in _predecessors.GetValueOrDefault(pass, ImmutableHashSet<GraphIdentity>.Empty))
            yield return new ExplicitPassOrder(predecessor, pass);
        foreach (GraphIdentity consumer in _successors.GetValueOrDefault(pass, ImmutableHashSet<GraphIdentity>.Empty))
            yield return new ExplicitPassOrder(pass, consumer);
    }

    public ImmutableHashSet<ExplicitPassOrder>.Enumerator GetEnumerator() => _edges.GetEnumerator();
    IEnumerator<ExplicitPassOrder> IEnumerable<ExplicitPassOrder>.GetEnumerator() => GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
    internal GraphOrders Orders { get; private init; } = new();
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
    internal Buffer? BoundResource;
    internal BufferRange? CurrentViewRange;
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
        BoundResource = BoundResource,
        CurrentViewRange = CurrentViewRange,
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
    internal Texture? BoundResource;
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
        BoundResource = BoundResource,
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
    internal GraphBufferInput? BufferInput;
    internal GraphTextureInput? TextureInput;
    internal GraphIdentity Texture;
    internal GraphIdentity AdditionalBuffer;
    internal BufferRange BufferRange;
    internal BufferRange? CurrentBufferRange;
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
    internal bool Enabled = true;
    internal required string Label;
    internal required GraphPassKind Kind;
    internal required PassQueueSelection Queue;
    internal required PassOptions Options;
    internal required PassCallbackStorage CallbackStorage;
    internal Pipeline? Pipeline;
    internal ShaderVariableLayout ParameterLayout = ShaderVariableLayout.Null;
    internal byte[] ParameterOrdinaryData = [];
    internal List<GraphParameterResourceBinding> ParameterBindings = [];
    internal HashSet<GraphIdentity> ViewReferences = [];
    internal List<GraphIdentity> PersistentBindings = [];
    internal int DeclarationOrdinal;
    internal List<GraphIdentity> Accesses = [];
    internal List<GraphColorAttachment> ColorAttachments = [];
    internal GraphDepthStencilAttachment? DepthStencilAttachment;
    internal List<PassRenderingRegion> RenderingRegions = [];

    internal GraphPass Clone() => new()
    {
        Enabled = Enabled,
        Label = Label,
        Kind = Kind,
        Queue = Queue,
        Options = Options,
        CallbackStorage = CallbackStorage,
        Pipeline = Pipeline,
        ParameterLayout = ParameterLayout,
        ParameterOrdinaryData = (byte[])ParameterOrdinaryData.Clone(),
        ParameterBindings = new List<GraphParameterResourceBinding>(ParameterBindings),
        ViewReferences = new HashSet<GraphIdentity>(ViewReferences),
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
    internal GraphBufferInput? BufferInput;
    internal GraphTextureInput? TextureInput;
    internal GraphParameterInput? ParameterInput;
    internal bool ParameterSecondary;
    internal BufferRange? CurrentBufferRange;
    internal TextureSubresourceRange? CurrentTextureRange;
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
    internal BufferContentScope BufferContentScope;
    internal UnorderedAccessHazardMode UnorderedAccessHazards;

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

