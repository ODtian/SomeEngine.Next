namespace SomeEngine.RenderGraph;

internal sealed class GraphRows<T>
{
    private readonly record struct Entry(uint Generation, int DenseIndex, int NextFree, bool Retired = false);
    private readonly Func<T, T>? _clone;
    private readonly GraphIdentityDomain _domain;
    private GraphTable<Entry> _entries;
    private GraphTable<T> _rows;
    private GraphTable<GraphIdentity> _identities;
    private HashSet<int> _writableRows = [];
    private int _firstFree = -1;
    // Issued identities survive a discarded edit. No later edit can revive an
    // identity returned by an earlier one.
    private List<uint> _issuedGenerations;

    internal GraphRows(GraphIdentityDomain domain, Func<T, T>? clone = null)
    {
        _domain = domain;
        _clone = clone;
        _entries = new();
        _rows = new();
        _identities = new();
        _issuedGenerations = [];
    }

    private GraphRows(GraphRows<T> source)
    {
        _domain = source._domain;
        _clone = source._clone;
        _entries = source._entries.Fork();
        _rows = source._rows.Fork();
        _identities = source._identities.Fork();
        _firstFree = source._firstFree;
        _issuedGenerations = source._issuedGenerations;
    }

    internal int Count => _rows.Count;
    internal GraphTable<T> Rows => _rows;
    internal GraphTable<GraphIdentity> Identities => _identities;
    internal object BlockIdentity => _rows.BlockIdentity;

    internal GraphRows<T> Fork()
    {
        // Pages are shared; mutable row values are copied only when that particular
        // row is edited. Submitted frames keep their earlier data.
        _writableRows = [];
        return new GraphRows<T>(this);
    }

    internal GraphIdentity Add(ulong owner, T value)
    {
        int index = _firstFree >= 0 ? _firstFree : _entries.Count;
        uint generation;
        if (index == _issuedGenerations.Count)
        {
            generation = 1;
            _issuedGenerations.Add(generation);
        }
        else
        {
            generation = checked(_issuedGenerations[index] + 1);
            _issuedGenerations[index] = generation;
        }
        if (_firstFree >= 0)
        {
            Entry entry = _entries[index];
            _firstFree = entry.NextFree;
            _entries.Set(index, new Entry(generation, Count, -1));
        }
        else
            _entries.Add(new Entry(generation, Count, -1));
        GraphIdentity identity = new(owner, index, generation, _domain);
        _rows.Add(value);
        _identities.Add(identity);
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
        if (!Contains(id))
            throw new ArgumentException("The graph identity is invalid, stale, or belongs to another graph.", nameof(id));
        return _entries[id.Slot].DenseIndex;
    }

    internal T Get(in GraphIdentity id) => _rows[DenseIndex(id)];

    internal T GetMutable(in GraphIdentity id)
    {
        int index = DenseIndex(id);
        if (_clone is not null && _writableRows.Add(id.Slot))
            _rows.Set(index, _clone(_rows[index]));
        return _rows[index];
    }

    internal void Set(in GraphIdentity id, T value)
    {
        _rows.Set(DenseIndex(id), value);
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
            _rows.Set(dense, _rows[last]);
            _identities.Set(dense, moved);
            _entries.Set(moved.Slot, _entries[moved.Slot] with { DenseIndex = dense });
        }
        _rows.RemoveLast();
        _identities.RemoveLast();
        Entry entry = _entries[id.Slot];
        bool retired = entry.Generation == uint.MaxValue;
        _entries.Set(id.Slot, new Entry(
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
internal sealed partial class GraphStructure
{
    internal GraphRows<GraphBuffer> Buffers { get; private init; } =
        new(GraphIdentityDomain.Buffer, static value => value.Clone());
    internal GraphRows<GraphTexture> Textures { get; private init; } =
        new(GraphIdentityDomain.Texture, static value => value.Clone());
    internal GraphRows<GraphPersistentParameterBindings> PersistentBindings { get; private init; } =
        new(GraphIdentityDomain.PersistentBindings, static value => value.Clone());
    internal GraphRows<GraphQueryPool> QueryPools { get; private init; } =
        new(GraphIdentityDomain.QueryPool, static value => value.Clone());
    internal GraphRows<GraphRayTracingShaderTable> ShaderTables { get; private init; } =
        new(GraphIdentityDomain.ShaderTable, static value => value.Clone());
    internal GraphRows<GraphView> Views { get; private init; } =
        new(GraphIdentityDomain.View, static value => value.Clone());
    internal GraphRows<GraphPass> Passes { get; private init; } =
        new(GraphIdentityDomain.Pass, static value => value.Clone());
    internal GraphRows<PassResourceAccess> Accesses { get; private init; } =
        new(GraphIdentityDomain.Access, static value => value.Clone());
    internal GraphRows<GraphExtensionPoint> ExtensionPoints { get; private init; } =
        new(GraphIdentityDomain.ExtensionPoint);
    internal GraphOrders Orders { get; private init; } = new();
    internal GraphRows<GraphRegion> Regions { get; private init; } =
        new(GraphIdentityDomain.Region, static value => value.Clone());
    internal GraphRegion RootRegion { get; private init; } = new() { Label = "Root" };
    internal GraphRegionId BufferUploadRegion;

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
            Regions = Regions.Fork(),
            RootRegion = RootRegion.Clone(),
            BufferUploadRegion = BufferUploadRegion,
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
        if (ReferenceEquals(Regions.BlockIdentity, other.Regions.BlockIdentity)) count++;
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
    internal GraphPassId BufferUploadPass;
    internal BufferBoundaryState[] BoundaryStates = [];
    internal string? Label => Description.Label;

    internal GraphBuffer Clone() => new()
    {
        BufferUploadPass = BufferUploadPass,
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

internal readonly record struct GraphExtensionPoint(string Label, long DeclarationOrdinal);

internal sealed class GraphPass
{
    internal bool Enabled = true;
    internal required string Label;
    internal required GraphPassKind Kind;
    internal required PassQueueSelection Queue;
    internal required PassOptions Options;
    internal required PassCallbackStorage CallbackStorage;
    internal Pipeline? Pipeline;
    internal VariableLayoutReflection ParameterLayout = VariableLayoutReflection.Null;
    internal byte[] ParameterOrdinaryData = [];
    internal List<GraphParameterResourceBinding> ParameterBindings = [];
    internal HashSet<GraphIdentity> ViewReferences = [];
    internal List<GraphIdentity> PersistentBindings = [];
    internal long DeclarationOrdinal;
    internal GraphIdentity Region;
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
        Region = Region,
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
    uint Height);

