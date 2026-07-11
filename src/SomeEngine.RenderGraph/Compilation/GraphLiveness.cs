namespace SomeEngine.RenderGraph;

/// <summary>
/// Exact value liveness for one frozen graph. This deliberately does not reuse execution-hazard
/// dependencies: only the latest producer of content read by a live pass can keep another pass alive.
/// </summary>
internal sealed class GraphLiveness
{
    private GraphLiveness(
        bool[] passes,
        bool[] roots,
        int[] retainingPasses,
        int[] activePassOrdinals,
        bool[] resources,
        bool[] bufferViews,
        bool[] textureViews)
    {
        Passes = passes;
        Roots = roots;
        RetainingPasses = retainingPasses;
        ActivePassOrdinals = activePassOrdinals;
        Resources = resources;
        BufferViews = bufferViews;
        TextureViews = textureViews;
    }

    public bool[] Passes { get; }
    public bool[] Roots { get; }
    public int[] RetainingPasses { get; }
    public int[] ActivePassOrdinals { get; }
    public bool[] Resources { get; }
    public bool[] BufferViews { get; }
    public bool[] TextureViews { get; }

    public static GraphLiveness Analyze(FrozenGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        ulong[][] bufferBoundaries = BuildBufferBoundaries(graph);
        int[][] bufferProducers = new int[graph.Resources.Length][];
        int[][] textureProducers = new int[graph.Resources.Length][];
        for (int resource = 0; resource < graph.Resources.Length; resource++)
        {
            FrozenResource value = graph.Resources[resource];
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                bufferProducers[resource] = Enumerable.Repeat(-1, Math.Max(0, bufferBoundaries[resource].Length - 1)).ToArray();
            }
            else
            {
                int planes = value.TextureDesc.Format == Format.D24UNormS8UInt ? 2 : 1;
                int cellCount = checked(value.TextureDesc.MipLevels * value.TextureDesc.ArrayLayers * planes);
                textureProducers[resource] = Enumerable.Repeat(-1, cellCount).ToArray();
            }
        }

        SortedSet<int>[] prerequisites = Enumerable.Range(0, graph.Passes.Length)
            .Select(static _ => new SortedSet<int>())
            .ToArray();
        bool[] roots = new bool[graph.Passes.Length];

        for (int pass = 0; pass < graph.Passes.Length; pass++)
        {
            foreach (FrozenAccess access in graph.Passes[pass].Accesses)
            {
                bool readsPriorValue = access.Effect != ResourceEffect.Write || access.PriorContents == PriorContents.Required;
                bool producesValue = access.Effect != ResourceEffect.Read;
                if (access.Kind == ResourceNodeKind.Buffer)
                {
                    VisitBufferSegments(access, bufferBoundaries[access.Resource], segment =>
                    {
                        int producer = bufferProducers[access.Resource][segment];
                        if (readsPriorValue && producer >= 0) prerequisites[pass].Add(producer);
                        if (producesValue) bufferProducers[access.Resource][segment] = pass;
                    });
                }
                else
                {
                    FrozenResource resource = graph.Resources[access.Resource];
                    foreach (TextureCell cell in EnumerateCells(resource.TextureDesc, access.TextureRange))
                    {
                        int index = cell.Index(resource.TextureDesc);
                        int producer = textureProducers[access.Resource][index];
                        if (readsPriorValue && producer >= 0) prerequisites[pass].Add(producer);
                        if (producesValue) textureProducers[access.Resource][index] = pass;
                    }
                }

                if (producesValue && graph.Resources[access.Resource].IsImported) roots[pass] = true;
            }
        }

        bool[] livePasses = new bool[graph.Passes.Length];
        int[] retainingPasses = Enumerable.Repeat(-1, graph.Passes.Length).ToArray();
        Stack<(int Pass, int Parent)> pending = new();
        for (int pass = roots.Length - 1; pass >= 0; pass--)
            if (roots[pass]) pending.Push((pass, -1));
        while (pending.TryPop(out (int Pass, int Parent) item))
        {
            int pass = item.Pass;
            if (livePasses[pass]) continue;
            livePasses[pass] = true;
            retainingPasses[pass] = item.Parent;
            foreach (int prerequisite in prerequisites[pass].Reverse()) pending.Push((prerequisite, pass));
        }

        int[] activePassOrdinals = Enumerable.Range(0, livePasses.Length).Where(pass => livePasses[pass]).ToArray();
        bool[] liveResources = new bool[graph.Resources.Length];
        bool[] liveBufferViews = new bool[graph.BufferViews.Length];
        bool[] liveTextureViews = new bool[graph.TextureViews.Length];
        foreach (int pass in activePassOrdinals)
        {
            foreach (FrozenAccess access in graph.Passes[pass].Accesses)
            {
                liveResources[access.Resource] = true;
                if (access.View < 0) continue;
                if (access.Kind == ResourceNodeKind.Buffer) liveBufferViews[access.View] = true;
                else liveTextureViews[access.View] = true;
            }
        }

        return new GraphLiveness(
            livePasses,
            roots,
            retainingPasses,
            activePassOrdinals,
            liveResources,
            liveBufferViews,
            liveTextureViews);
    }

    private static ulong[][] BuildBufferBoundaries(FrozenGraph graph)
    {
        List<ulong>?[] values = new List<ulong>?[graph.Resources.Length];
        foreach (FrozenPass pass in graph.Passes)
        foreach (FrozenAccess access in pass.Accesses)
        {
            if (access.Kind != ResourceNodeKind.Buffer) continue;
            List<ulong> boundaries = values[access.Resource] ??= new List<ulong>();
            boundaries.Add(access.BufferRange.Offset);
            boundaries.Add(checked(access.BufferRange.Offset + access.BufferRange.Size));
        }

        ulong[][] result = new ulong[graph.Resources.Length][];
        for (int resource = 0; resource < result.Length; resource++)
        {
            result[resource] = values[resource] is { } boundaries
                ? boundaries.Distinct().Order().ToArray()
                : [];
        }
        return result;
    }

    private static void VisitBufferSegments(in FrozenAccess access, ulong[] boundaries, Action<int> visit)
    {
        ulong end = checked(access.BufferRange.Offset + access.BufferRange.Size);
        int first = Array.BinarySearch(boundaries, access.BufferRange.Offset);
        int afterLast = Array.BinarySearch(boundaries, end);
        if (first < 0 || afterLast < 0)
            throw new InvalidOperationException("Normalized buffer access boundaries are missing from the liveness partition.");
        for (int segment = first; segment < afterLast; segment++) visit(segment);
    }

    private static IEnumerable<TextureCell> EnumerateCells(TextureDesc desc, TextureSubresourceRange range)
    {
        TextureAspect[] aspects = [TextureAspect.Color, TextureAspect.Depth, TextureAspect.Stencil];
        for (int layer = range.FirstLayer; layer < range.FirstLayer + range.LayerCount; layer++)
        for (int mip = range.FirstMip; mip < range.FirstMip + range.MipCount; mip++)
        foreach (TextureAspect aspect in aspects)
            if ((range.Aspect & aspect) != 0)
                yield return new TextureCell(mip, layer, aspect);
    }

    private readonly record struct TextureCell(int Mip, int Layer, TextureAspect Aspect)
    {
        public int Index(in TextureDesc desc)
        {
            int plane = Aspect == TextureAspect.Stencil ? 1 : 0;
            return checked(Mip + Layer * desc.MipLevels + plane * desc.MipLevels * desc.ArrayLayers);
        }
    }
}
