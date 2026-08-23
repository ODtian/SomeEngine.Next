namespace SomeEngine.RenderGraph;

internal sealed class GraphStructureIndex
{
    internal static readonly GraphStructureIndex Empty = new(
        new GraphStructure(),
        [], [], [], [], [], [], [], [], []);

    private GraphStructureIndex(
        GraphStructure structure,
        GraphIdentity[] bufferIds,
        GraphIdentity[] textureIds,
        GraphIdentity[] queryPoolIds,
        GraphIdentity[] shaderTableIds,
        GraphIdentity[] viewIds,
        GraphIdentity[] passIds,
        GraphIdentity[] accessIds,
        int[][] passAccesses,
        int[][] predecessors)
    {
        Structure = structure;
        BufferIds = bufferIds;
        TextureIds = textureIds;
        QueryPoolIds = queryPoolIds;
        ShaderTableIds = shaderTableIds;
        ViewIds = viewIds;
        PassIds = passIds;
        AccessIds = accessIds;
        PassAccessIndices = passAccesses;
        ExplicitPredecessors = predecessors;
    }

    internal GraphStructure Structure { get; }
    internal GraphIdentity[] BufferIds { get; }
    internal GraphIdentity[] TextureIds { get; }
    internal GraphIdentity[] QueryPoolIds { get; }
    internal GraphIdentity[] ShaderTableIds { get; }
    internal GraphIdentity[] ViewIds { get; }
    internal GraphIdentity[] PassIds { get; }
    internal GraphIdentity[] AccessIds { get; }
    internal int[][] PassAccessIndices { get; }
    internal int[][] ExplicitPredecessors { get; }

    internal static GraphStructureIndex Build(
        RenderGraph graph,
        GraphStructure structure)
    {
        int bufferCount = structure.Buffers.Count;
        int textureCount = structure.Textures.Count;
        int queryPoolCount = structure.QueryPools.Count;
        int shaderTableCount = structure.ShaderTables.Count;
        int viewCount = structure.Views.Count;
        int passCount = structure.Passes.Count;
        int accessCount = structure.Accesses.Count;

        var bufferIds = new GraphIdentity[bufferCount];
        var textureIds = new GraphIdentity[textureCount];
        var queryPoolIds = new GraphIdentity[queryPoolCount];
        var shaderTableIds = new GraphIdentity[shaderTableCount];
        var viewIds = new GraphIdentity[viewCount];
        var passIds = new GraphIdentity[passCount];
        var accessIds = new GraphIdentity[accessCount];

        for (int i = 0; i < bufferCount; i++)
        {
            bufferIds[i] = structure.Buffers.IdentityAt(graph.Identity, i);
            GraphBuffer buffer = structure.Buffers.Rows[i];
            ValidateBuffer(buffer);
            buffer.Requirements = graph.Backend.GetBufferMemoryRequirements(
                graph.Device,
                buffer.Description,
                buffer.MemoryType);
        }

        for (int i = 0; i < textureCount; i++)
        {
            textureIds[i] = structure.Textures.IdentityAt(graph.Identity, i);
            GraphTexture texture = structure.Textures.Rows[i];
            ValidateTexture(texture);
            TextureDesc description = texture.BorrowDescription();
            texture.Requirements = graph.Backend.GetTextureMemoryRequirements(
                graph.Device,
                description);
        }

        for (int i = 0; i < queryPoolCount; i++)
        {
            queryPoolIds[i] = structure.QueryPools.IdentityAt(graph.Identity, i);
            GraphQueryPool pool = structure.QueryPools.Rows[i];
            if (!ReferenceEquals(pool.Resource.Device, graph.Device))
                throw new ArgumentException("QueryPool belongs to another Device.");
            if (pool.Resource.Description.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(pool.Resource.Description.Count));
        }

        for (int i = 0; i < shaderTableCount; i++)
        {
            shaderTableIds[i] = structure.ShaderTables.IdentityAt(graph.Identity, i);
            GraphRayTracingShaderTable table = structure.ShaderTables.Rows[i];
            if (!ReferenceEquals(table.Resource.Device, graph.Device))
                throw new ArgumentException("RayTracingShaderTable belongs to another Device.");
            foreach (ref readonly GraphParameterResourceBinding binding in table.Inventory.AsSpan())
                binding.ValidateStatic(structure, graph.Device);
        }

        for (int i = 0; i < viewCount; i++)
        {
            viewIds[i] = structure.Views.IdentityAt(graph.Identity, i);
            ValidateView(graph, structure, structure.Views.Rows[i]);
        }

        foreach (GraphPersistentParameterBindings bindings in structure.PersistentBindings.Rows)
        {
            if (!ReferenceEquals(bindings.Resource.Device, graph.Device))
                throw new ArgumentException("PersistentParameterBindings belong to another Device.");
            foreach (ref readonly GraphParameterResourceBinding binding in bindings.Inventory.AsSpan())
                binding.ValidateStatic(structure, graph.Device);
        }

        for (int i = 0; i < passCount; i++)
        {
            passIds[i] = structure.Passes.IdentityAt(graph.Identity, i);
            GraphPass pass = structure.Passes.Rows[i];
            pass.DeclarationOrdinal = i;
            ValidatePass(graph, pass);
            foreach (GraphIdentity bindings in pass.PersistentBindings)
                if (!structure.PersistentBindings.Contains(bindings))
                    throw new ArgumentException("A Pass references stale PersistentParameterBindings.");
        }

        for (int i = 0; i < accessCount; i++)
        {
            accessIds[i] = structure.Accesses.IdentityAt(graph.Identity, i);
            ValidateAccess(structure, structure.Accesses.Rows[i]);
        }

        var passAccesses = new int[passCount][];
        for (int passIndex = 0; passIndex < passCount; passIndex++)
        {
            GraphPass pass = structure.Passes.Rows[passIndex];
            int[] dense = new int[pass.Accesses.Count];
            for (int i = 0; i < dense.Length; i++)
                dense[i] = structure.Accesses.DenseIndex(pass.Accesses[i]);
            passAccesses[passIndex] = dense;
        }

        var predecessorLists = new List<int>[passCount];
        var successorLists = new List<int>[passCount];
        for (int i = 0; i < passCount; i++)
        {
            predecessorLists[i] = [];
            successorLists[i] = [];
        }
        foreach (ExplicitPassOrder order in structure.Orders)
        {
            int predecessor = structure.Passes.DenseIndex(order.Predecessor);
            int consumer = structure.Passes.DenseIndex(order.Consumer);
            AddUnique(predecessorLists[consumer], predecessor);
            AddUnique(successorLists[predecessor], consumer);
        }
        ValidateAcyclic(predecessorLists, successorLists);

        return new GraphStructureIndex(
            structure,
            bufferIds,
            textureIds,
            queryPoolIds,
            shaderTableIds,
            viewIds,
            passIds,
            accessIds,
            passAccesses,
            ToArrays(predecessorLists));
    }

    private static int[][] ToArrays(List<int>[] values)
    {
        var result = new int[values.Length][];
        for (int i = 0; i < result.Length; i++)
        {
            values[i].Sort();
            result[i] = values[i].ToArray();
        }
        return result;
    }

    private static void AddUnique(List<int> values, int value)
    {
        if (!values.Contains(value)) values.Add(value);
    }

    private static void ValidateAcyclic(List<int>[] predecessors, List<int>[] successors)
    {
        int count = predecessors.Length;
        var indegree = new int[count];
        var ready = new PriorityQueue<int, int>();
        for (int i = 0; i < count; i++)
        {
            indegree[i] = predecessors[i].Count;
            if (indegree[i] == 0) ready.Enqueue(i, i);
        }
        int visited = 0;
        while (ready.TryDequeue(out int pass, out _))
        {
            visited++;
            foreach (int successor in successors[pass])
                if (--indegree[successor] == 0)
                    ready.Enqueue(successor, successor);
        }
        if (visited != count)
            throw new InvalidOperationException("RG5001: The render graph contains an explicit-order cycle.");
    }

    private static void ValidateBuffer(GraphBuffer buffer)
    {
        if (buffer.Description.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(buffer.Description.Size));
        if (buffer.Description.Usages == BufferUsages.None)
            throw new ArgumentOutOfRangeException(nameof(buffer.Description.Usages));
        if (!Enum.IsDefined(buffer.MemoryType))
            throw new ArgumentOutOfRangeException(nameof(buffer.MemoryType));
        if (buffer.Ownership == RenderGraphResourceOwnership.GraphOwned &&
            buffer.Lifetime == RenderGraphResourceLifetime.PerFrame &&
            buffer.Description.Usages.HasFlag(BufferUsages.Shareable))
        {
            throw new NotSupportedException("Transient shareable buffers are not supported.");
        }
    }

    private static void ValidateTexture(GraphTexture texture)
    {
        if (texture.Width == 0 || texture.Height == 0 || texture.Depth == 0 ||
            texture.MipLevelCount == 0 || texture.ArrayLayerCount == 0 ||
            texture.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(texture));
        }
        if (texture.Usages == TextureUsages.None)
            throw new ArgumentOutOfRangeException(nameof(texture.Usages));
        if (texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
            texture.Lifetime == RenderGraphResourceLifetime.PerFrame &&
            texture.Usages.HasFlag(TextureUsages.Shareable))
        {
            throw new NotSupportedException("Transient shareable textures are not supported.");
        }
    }

    private static void ValidateView(RenderGraph graph, GraphStructure structure, GraphView view)
    {
        switch (view.Kind)
        {
            case GraphViewKind.BufferCbv:
            case GraphViewKind.BufferSrv:
            case GraphViewKind.BufferUav:
                GraphBuffer buffer = structure.Buffers.Get(view.Buffer);
                _ = ResolveRange(view.BufferRange, buffer.Description.Size);
                if (view.AdditionalBuffer.IsValid)
                {
                    GraphBuffer counter = structure.Buffers.Get(view.AdditionalBuffer);
                    if (view.CounterOffset > counter.Description.Size ||
                        counter.Description.Size - view.CounterOffset < sizeof(uint))
                    {
                        throw new ArgumentOutOfRangeException(nameof(view.CounterOffset));
                    }
                }
                break;
            case GraphViewKind.TextureSrv:
            case GraphViewKind.TextureUav:
            case GraphViewKind.ColorAttachment:
            case GraphViewKind.DepthStencil:
                ValidateTextureRange(structure.Textures.Get(view.Texture), view.TextureRange);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(view.Kind));
        }
    }

    private static void ValidatePass(RenderGraph graph, GraphPass pass)
    {
        if (string.IsNullOrWhiteSpace(pass.Label))
            throw new ArgumentException("A pass label cannot be empty.", nameof(pass));
        if (!Enum.IsDefined(pass.Kind)) throw new ArgumentOutOfRangeException(nameof(pass.Kind));
        if (!Enum.IsDefined(pass.Options.Culling) ||
            !Enum.IsDefined(pass.Options.Scheduling) ||
            !Enum.IsDefined(pass.Options.Recording) ||
            !Enum.IsDefined(pass.Options.RasterMerging))
        {
            throw new ArgumentOutOfRangeException(nameof(pass.Options));
        }
        _ = graph.ResolveQueueCandidates(pass.Queue, pass.Kind);
    }

    private static void ValidateAccess(GraphStructure structure, PassResourceAccess access)
    {
        if (!Enum.IsDefined(access.Mode) || !Enum.IsDefined(access.Coverage))
            throw new ArgumentOutOfRangeException(nameof(access));
        bool physicalResource = access.TargetKind is
            GraphAccessTargetKind.Buffer or GraphAccessTargetKind.Texture;
        if (physicalResource &&
            (access.Sync == PipelineSync.None || access.Access == ResourceAccess.NoAccess))
            throw new ArgumentException("Pass access must name a real synchronization and access scope.");
        switch (access.TargetKind)
        {
            case GraphAccessTargetKind.Buffer:
                GraphBuffer buffer = structure.Buffers.Get(access.Target);
                access.BufferRange = ResolveRange(access.BufferRange, buffer.Description.Size);
                ValidateMode(access);
                break;
            case GraphAccessTargetKind.Texture:
                GraphTexture texture = structure.Textures.Get(access.Target);
                ValidateTextureRange(texture, access.TextureRange);
                ValidateMode(access);
                break;
            case GraphAccessTargetKind.QueryPool:
                GraphQueryPool pool = structure.QueryPools.Get(access.Target);
                if (access.QueryRange.QueryCount == 0 ||
                    access.QueryRange.FirstQuery >= pool.Resource.Description.Count ||
                    access.QueryRange.QueryCount >
                        pool.Resource.Description.Count - access.QueryRange.FirstQuery)
                    throw new ArgumentOutOfRangeException(nameof(access.QueryRange));
                break;
            case GraphAccessTargetKind.RayTracingShaderTable:
                _ = structure.ShaderTables.Get(access.Target);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(access.TargetKind));
        }
    }

    private static void ValidateMode(PassResourceAccess access)
    {
        bool writes = ResourceAccessRules.Writes(access.Access);
        if (access.Mode == GraphAccessMode.Read && writes)
            throw new ArgumentException("A Read declaration contains a write access bit.");
        if (access.Mode != GraphAccessMode.Read && !writes)
            throw new ArgumentException("A Write declaration does not contain a write access bit.");
    }

    internal static BufferRange ResolveRange(in BufferRange range, ulong size)
    {
        if (range.IsWhole) return new BufferRange(0, size);
        if (range.Size == 0 || range.Offset > size || range.Size > size - range.Offset)
            throw new ArgumentOutOfRangeException(nameof(range));
        return range;
    }

    internal static void ValidateTextureRange(GraphTexture texture, in TextureSubresourceRange range)
    {
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            range.FirstMipLevel >= texture.MipLevelCount ||
            range.MipLevelCount > texture.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= texture.ArrayLayerCount ||
            range.ArrayLayerCount > texture.ArrayLayerCount - range.FirstArrayLayer ||
            range.Aspects == TextureAspects.None)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
    }
}

