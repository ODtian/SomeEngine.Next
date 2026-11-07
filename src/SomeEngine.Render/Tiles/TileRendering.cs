using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tiles;

public readonly record struct TileRenderingDescription(
    uint CellCount, uint ViewCount, uint TriangleCapacity, uint ReferenceCapacity,
    uint TriangleStride, uint SourceBoundsStride, bool IndexedColor, bool AdaptiveDepth,
    bool HardwareCoverage, bool HardwareBinning, bool PixelLists, bool Measure,
    ulong PixelCapacity, bool Indexed);

/// <summary>Persistent Tile resources and the indexing, coverage and color pass chains.</summary>
public sealed class TileRendering : IRenderGraphFrameExecutionObserver
{
    private readonly TileRenderingDescription _description;
    private readonly Dictionary<string, (GraphBufferId Buffer, uint Stride)> _buffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGraphResourceProvider> _providers = new(StringComparer.Ordinal);
    private GraphPassId _coverageReady;
    private GraphRegionId Geometry { get; }
    private GraphRegionId Coverage { get; }
    private GraphRegionId Requests { get; }
    private GraphRegionId Color { get; }
    public uint TriangleCapacity => _description.TriangleCapacity;
    public bool AdaptiveDepth => _description.AdaptiveDepth;
    public bool HardwareBinning => _description.HardwareBinning;
    public GraphPassId GeometryReady { get; private set; }
    public GraphPassId[] Builds { get; private set; } = [];
    public GraphPassId[] CoveragePasses { get; private set; } = [];
    public GraphPassId[] RequestPasses { get; private set; } = [];
    public GraphPassId[] ColorPasses { get; private set; } = [];
    private readonly Dictionary<GraphPassId, string> _passNames = [];
    public IReadOnlyDictionary<GraphPassId, string> PassNames => _passNames;
    public GraphPassId ColorPass => ColorPasses[^1];
    public bool HasSubmittedGeometry { get; private set; }
    public bool ReusedGeometry { get; private set; }
    public GraphBufferId Triangles { get; }
    public GraphBufferId TriangleCount { get; }
    public GraphBufferId Counts { get; }
    public GraphBufferId References { get; }
    private GraphBufferId Arguments { get; }
    public GraphBufferId Offsets { get; }
    private GraphBufferId Writes { get; }
    public GraphBufferId Streams { get; }
    private GraphBufferId ProjectedSources { get; }
    private GraphBufferId SortScratch { get; }
    private GraphBufferId SortJobs { get; }
    private GraphBufferId CellWork { get; }
    private GraphBufferId CellTrees { get; }
    private GraphBufferId DepthNodes { get; }
    private GraphBufferId CoverageRanges { get; }
    private GraphBufferId HardwareMasks { get; }
    private GraphBufferId HardwareAssociations { get; }
    private GraphBufferId TilePartitions { get; }
    private GraphBufferId DepthCells { get; }
    private GraphBufferId DepthReferences { get; }
    private GraphBufferId ReferenceDepths { get; }
    public GraphBufferId TileWork { get; }
    private GraphBufferId PixelHeads { get; }
    private GraphBufferId Fragments { get; }
    public GraphBufferId FragmentCount { get; }

    public TileRendering(ref RenderGraphEdit edit, in TileRenderingDescription description,
        GraphRegionId geometry, GraphRegionId coverage, GraphRegionId requests, GraphRegionId color)
    {
        _description = description;
        Geometry = edit.AddRegion("Tile geometry and indexing", geometry);
        Coverage = edit.AddRegion("Tile hardware coverage", coverage);
        Requests = edit.AddRegion("Tile shadow requests", requests);
        Color = edit.AddRegion("Tile color", color);
        uint tiles = description.CellCount, capacity = description.TriangleCapacity;
        bool adaptive = description.AdaptiveDepth, hardwareBinning = description.HardwareBinning;
        bool pixelLists = description.PixelLists, tileColor = description.IndexedColor;
        ulong pixels = description.PixelCapacity;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        BufferUsages counted = storage | (description.Measure ? BufferUsages.CopySource : BufferUsages.None);
        BufferDesc triangles = new((ulong)capacity * description.TriangleStride, storage, "Tile projected triangles");
        BufferDesc counts = new((ulong)tiles * 4u, counted, "Tile counts");
        BufferDesc references = new((ulong)description.ReferenceCapacity * 4u, storage, "Tile references");
        BufferDesc triangleCount = new(20, counted, "Tile triangle count");
        BufferDesc arguments = new(172, storage | BufferUsages.Indirect, "Tile arguments");
        Triangles = edit.CreatePersistentBuffer(triangles);
        _buffers.Add("TransparencyTriangles", (Triangles, description.TriangleStride));
        _buffers.Add("TransparencyRasterTriangles", (Triangles, description.TriangleStride));
        TriangleCount = edit.CreatePersistentBuffer(triangleCount);
        _buffers.Add("TransparencyTriangleCount", (TriangleCount, 0));
        Counts = edit.CreatePersistentBuffer(counts with { Size = counts.Size * 2u });
        _buffers.Add("TransparencyTileCounts", (Counts, 4));
        References = edit.CreatePersistentBuffer(references);
        _buffers.Add("TransparencyReferences", (References, 4));
        Arguments = edit.CreatePersistentBuffer(arguments);
        _buffers.Add("TransparencyArguments", (Arguments, 0));
        Offsets = edit.CreatePersistentBuffer(counts with { Label = "Tile cell offsets" });
        _buffers.Add("TransparencyCellOffsets", (Offsets, 4));
        Writes = edit.CreatePersistentBuffer(counts with { Label = "Tile cell writes" });
        _buffers.Add("TransparencyCellWrites", (Writes, 4));
        Streams = edit.CreatePersistentBuffer(counts with { Label = "Tile streaming cells" });
        _buffers.Add("TransparencyCellStreams", (Streams, 4));
        ProjectedSources = edit.CreatePersistentBuffer(new BufferDesc((ulong)capacity * description.SourceBoundsStride, storage, "Tile source bounds"));
        _buffers.Add("TransparencyProjectedSources", (ProjectedSources, description.SourceBoundsStride));
        SortScratch = edit.CreatePersistentBuffer(references with { Size = references.Size * (adaptive ? 3u : 1u), Label = "Tile merge references" });
        _buffers.Add("TransparencySortScratch", (SortScratch, 4));
        SortJobs = edit.CreatePersistentBuffer(new BufferDesc(16u + (ulong)tiles * 64u, storage, "Tile sort jobs"));
        _buffers.Add("TransparencySortJobs", (SortJobs, 0));
        CellWork = edit.CreatePersistentBuffer(new BufferDesc(16u + (ulong)tiles * 8u + description.ViewCount * 32u, storage, "Tile occupied cells"));
        _buffers.Add("TransparencyCellWork", (CellWork, 0));
        CellTrees = edit.CreatePersistentBuffer(new BufferDesc((ulong)tiles * 8u, storage, "Tile cell trees"));
        _buffers.Add("TransparencyCellTrees", (CellTrees, 8));
        DepthNodes = edit.CreatePersistentBuffer(new BufferDesc(((ulong)description.ReferenceCapacity * (adaptive ? 3u : 1u) / 8u + (ulong)tiles * (adaptive ? 68u : 2u)) * 16u,
                storage, "Tile XY and depth nodes"));
        _buffers.Add("TransparencyDepthNodes", (DepthNodes, 16));
        CoverageRanges = edit.CreatePersistentBuffer(new BufferDesc(description.HardwareCoverage && !pixelLists ? (ulong)capacity * 16u : 16u,
                storage, "Tile hardware triangle ranges"));
        _buffers.Add("TransparencyCoverageRanges", (CoverageRanges, 16));
        HardwareMasks = edit.CreatePersistentBuffer(new BufferDesc(description.HardwareCoverage && !pixelLists ? (ulong)description.ReferenceCapacity * 32u : 4u,
                storage, "Tile hardware coverage"));
        _buffers.Add("TransparencyHardwareMasks", (HardwareMasks, 4));
        HardwareAssociations = edit.CreatePersistentBuffer(new BufferDesc(hardwareBinning ? (ulong)description.ReferenceCapacity * 8u : 8u,
                storage, "Tile visible hardware associations"));
        _buffers.Add("TransparencyHardwareAssociations", (HardwareAssociations, 8));
        TilePartitions = edit.CreatePersistentBuffer(new BufferDesc((ulong)tiles * 16u, storage, "Tile depth distributions"));
        _buffers.Add("TransparencyTilePartitions", (TilePartitions, 16));
        DepthCells = edit.CreatePersistentBuffer(new BufferDesc(adaptive ? (ulong)tiles * 16u * 16u : 16u, storage, "Tile adaptive depth cells"));
        _buffers.Add("TransparencyDepthCells", (DepthCells, 16));
        DepthReferences = edit.CreatePersistentBuffer(new BufferDesc(adaptive ? references.Size * 2u : 4u, storage, "Tile adaptive depth references"));
        _buffers.Add("TransparencyDepthReferences", (DepthReferences, 4));
        ReferenceDepths = edit.CreatePersistentBuffer(new BufferDesc(tileColor ? (ulong)description.ReferenceCapacity * 8u * (adaptive ? 3u : 1u) : 8u, storage, "Tile reference depths"));
        _buffers.Add("TransparencyReferenceDepths", (ReferenceDepths, 8));
        TileWork = edit.CreatePersistentBuffer(new BufferDesc(32u + (ulong)tiles * 12u, counted, "Tile modes and color work"));
        _buffers.Add("TransparencyTileWork", (TileWork, 0));
        PixelHeads = pixelLists ? edit.CreatePersistentBuffer(new BufferDesc(pixels * 4u, storage, "PPLL reference heads")) : default;
        _buffers.Add("TransparencyPixelHeads", (PixelHeads, 4));
        Fragments = pixelLists ? edit.CreatePersistentBuffer(new BufferDesc(Math.Max((ulong)description.ReferenceCapacity, pixels * 64u) * 16u,
                storage, "PPLL reference fragments")) : default;
        _buffers.Add("TransparencyFragments", (Fragments, 16));
        FragmentCount = pixelLists ? edit.CreatePersistentBuffer(new BufferDesc(4u, counted, "PPLL reference fragment count")) : default;
        _buffers.Add("TransparencyFragmentCount", (FragmentCount, 0));
    }

    public IGraphResourceProvider GetResources(string entry)
    {
        if (!_providers.TryGetValue(entry, out IGraphResourceProvider? provider))
            _providers.Add(entry, provider = new PassResources(this, entry));
        return provider;
    }

    public void Install(ref RenderGraphEdit edit, TileShaders shaders, TilePassInputs inputs,
        bool splitSorting, bool requestShadows, string[] colorEntries, GraphPassId stencilReady, GraphResourceBinder connect)
    {
        uint tiles = _description.CellCount;
        ulong pixels = _description.PixelCapacity;
        bool tileColor = _description.IndexedColor, hardwareCoverage = _description.HardwareCoverage;
        bool pixelLists = _description.PixelLists, hardwareBinning = _description.HardwareBinning;
        var builds = new List<GraphPassId>();
        var coveragePasses = new List<GraphPassId>();
        GraphPassId geometryReady = default;
        AddBuild(ref edit, "ClearTransparentTiles", tiles);
        AddBuild(ref edit, "CopyTransparentSources", 0, true, 12);
        AddBuild(ref edit, "BuildTransparentGeometry", 0, true, 0);
        if (!pixelLists)
        {
            AddBuild(ref edit, "CountTransparentCells", 0, true, 36);
            AddBuild(ref edit, "ReserveTransparentCells", tiles);
            if (hardwareBinning)
            {
                AddHardwareCoverage(ref edit);
                AddBuild(ref edit, "CountTransparentHardwareCells", 0, true, 124, Coverage);
                AddBuild(ref edit, "ReserveTransparentCells", tiles, region:Coverage);
                AddBuild(ref edit, "ScatterTransparentHardwareCells", 0, true, 124, Coverage);
            }
            else AddBuild(ref edit, "ScatterTransparentCells", 0, true, 36);
            if (splitSorting)
            {
                AddBuild(ref edit, "PrepareTransparentCells", 0, true, 60, hardwareBinning ? Coverage : Geometry);
                AddBuild(ref edit, "FinalizeTransparentSort", 1, region:hardwareBinning ? Coverage : Geometry);
                AddBuild(ref edit, "SortTransparentRanges", 0, true, 148, hardwareBinning ? Coverage : Geometry);
            }
            else AddBuild(ref edit, "SortTransparentCells", 0, true, 60, hardwareBinning ? Coverage : Geometry);
            AddBuild(ref edit, "ClassifyTransparentTiles", tiles, region:hardwareBinning ? Coverage : Geometry);
            AddBuild(ref edit, "FinalizeTransparentTiles", 1, region:hardwareBinning ? Coverage : Geometry);
        }
        Builds = builds.ToArray();
        GeometryReady = geometryReady;
#if SOMEENGINE_TRANSPARENCY_RESEARCH
        if (pixelLists)
        {
            GraphPassId clear = BuildPass(ref edit, "PrepareTransparentPixelLists", Coverage, new DispatchArguments(((uint)pixels + 63u) / 64u, 1, 1), null);
            edit.OrderAfter(clear, geometryReady);
            GraphPassId coverage = hardwareCoverage ? BuildCoverage(ref edit)
                : BuildPass(ref edit, "BuildTransparentPixelLists",
                    Coverage, default, 36);
            edit.OrderAfter(coverage, clear);
            coveragePasses.AddRange([clear, coverage]);
            geometryReady = coverage;
        }
        else
#endif
        if (hardwareCoverage && tileColor && !hardwareBinning) AddHardwareCoverage(ref edit);
        CoveragePasses = coveragePasses.ToArray();
        _coverageReady = geometryReady;
        if (requestShadows)
        {
            GraphPassId request = BuildPass(ref edit, "RequestTransparentPages", Requests, default, 160);
            edit.OrderAfter(request, geometryReady);
            RequestPasses = [request];
        }
        SetColorPasses(ref edit, shaders, inputs, colorEntries, stencilReady, connect);

        GraphPassId BuildPass(ref RenderGraphEdit graph, string entry, GraphRegionId region,
            DispatchArguments groups, uint? indirectOffset) =>
            AddComputePass(ref graph, shaders[entry], entry, region, groups, indirectOffset, inputs.DispatchLayout, connect);

        GraphPassId BuildCoverage(ref RenderGraphEdit graph) => AddHardwarePass(ref graph, shaders, inputs, connect);

        void AddBuild(ref RenderGraphEdit graph, string name, uint work, bool indirect = false, uint offset = 0, GraphRegionId region = default)
        {
            GraphPassId pass = BuildPass(ref graph, name,
                region == default ? Geometry : region, new DispatchArguments((work + 63u) / 64u, 1, 1), indirect ? offset : null);
            if (geometryReady != default) graph.OrderAfter(pass, geometryReady);
            builds.Add(pass);
            geometryReady = pass;
        }

        void AddHardwareCoverage(ref RenderGraphEdit graph)
        {
            GraphPassId clear = BuildPass(ref graph, "ClearTransparentHardwareMasks", Coverage, default, 88);
            graph.OrderAfter(clear, geometryReady);
            GraphPassId pass = BuildCoverage(ref graph);
            graph.OrderAfter(pass, clear);
            coveragePasses.AddRange([clear, pass]);
            geometryReady = pass;
        }

    }

    public void SetColorPasses(ref RenderGraphEdit edit, TileShaders shaders, TilePassInputs inputs,
        string[] entries, GraphPassId stencilReady, GraphResourceBinder connect)
    {
        if (ColorPasses.Select(pass => _passNames[pass]).SequenceEqual(entries)) return;
        foreach (GraphPassId pass in ColorPasses)
        {
            edit.Remove(pass);
            _passNames.Remove(pass);
        }
        var passes = new GraphPassId[entries.Length];
        for (int index = 0; index < entries.Length; index++)
        {
            string entry = entries[index];
            passes[index] = AddComputePass(ref edit, shaders[entry], entry, Color,
                default, GetPixelDispatchOffset(entry), inputs.DispatchLayout, connect);
            edit.OrderAfter(passes[index], index == 0 ? _coverageReady : passes[index - 1]);
            if (index == 0) edit.OrderAfter(passes[index], stencilReady);
        }
        ColorPasses = passes;
    }

    private static uint GetPixelDispatchOffset(string entry) => entry switch
    {
        "ResolveTileTransparentColor" => 100,
        "ResolveLocalTileTransparentColor" => 112,
        "ResolveDepthTileTransparentColor" => 136,
        "ResolveProjectedTransparentColor" or "ResolveProjectedUnorderedTransparentColor" or
            "ResolveProjectedOrderedTransparentColor" => 48,
        _ => 24,
    };

    private GraphPassId AddComputePass(ref RenderGraphEdit graph, TileComputeShader compiled,
        string entry, GraphRegionId region, DispatchArguments groups, uint? indirectOffset,
        IndirectCommandLayout dispatchLayout, GraphResourceBinder connect)
    {
        var pipeline = compiled.Pipeline;
        GraphPassId id;
        if (indirectOffset is { } offset)
        {
            var data = new IndirectDispatch(dispatchLayout, Arguments, offset);
            id = graph.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics), data,
                new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
                static (ref PassDefinition pass, ref IndirectDispatch value) =>
                    _ = pass.Read(value.Arguments, new BufferRange(value.Offset, 12),
                        PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                IndirectDispatch.Record, region: region);
        }
        else
        {
            id = graph.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics), new DirectDispatch(groups),
                new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
                static (ref PassDefinition _, ref DirectDispatch _) => { }, DirectDispatch.Record, region: region);
        }
        _passNames.Add(id, entry);
        PassDefinition access = graph.GetPass(id);
        connect(ref graph, ref access, PipelineSync.ComputeShading);
        return id;
    }

    private GraphPassId AddHardwarePass(ref RenderGraphEdit graph, TileShaders shaders,
        TilePassInputs inputs, GraphResourceBinder connect)
    {
        Pipeline pipeline = shaders.GetHardwarePipeline(_description.PixelLists);
        int target = shaders.HardwareShader.RequireSlangTarget(pipeline.Device.Capabilities.CompileTarget);
        VariableLayoutReflection layout = shaders.HardwareShader.GetSlangReflection(target).GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        var data = new HardwareCoverage(inputs.DrawLayout, Arguments, inputs.Width, inputs.Height);
        GraphPassId id = graph.AddRasterFramePass("Tile hardware coverage",
            PassQueueSelection.AnyOfType(QueueType.Graphics), data, new PassOptions(RepeatCommands: true),
            pipeline, layout, default,
            static (ref PassDefinition pass, ref HardwareCoverage value) =>
            {
                pass.DefineRasterExtent((uint)value.Width, (uint)value.Height);
                pass.Read(value.Arguments, new BufferRange(72, 16), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
            }, HardwareCoverage.Record, region: Coverage);
        _passNames.Add(id, "RasterTransparentHardwareCoverage");
        PassDefinition access = graph.GetPass(id);
        connect(ref graph, ref access, PipelineSync.VertexShading | PipelineSync.PixelShading);
        GraphDepthStencilViewId depth = graph.CreateDepthStencilView(inputs.Depth,
            new TextureSubresourceRange(0, 1, 0, _description.ViewCount, TextureAspects.Depth),
            Format.D32Float, TextureViewDimension.Texture2DArray, readOnlyDepth: true);
        access.DepthStencilAttachment(depth, LoadType.Load, StoreType.Store, WriteCoverage.Partial, 0,
            LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
        return id;
    }

    private readonly record struct DirectDispatch(DispatchArguments Groups)
    {
        internal static void Record(ref ComputePassCommandScope commands, in DirectDispatch data) => commands.Dispatch(data.Groups);
    }

    private readonly record struct IndirectDispatch(IndirectCommandLayout Layout, GraphBufferId Arguments, uint Offset)
    {
        internal static void Record(ref ComputePassCommandScope commands, in IndirectDispatch data) =>
            commands.ExecuteIndirect(data.Layout, new BufferRegion(commands.GetBuffer(data.Arguments), new BufferRange(data.Offset, 12)), 1);
    }

    private readonly record struct HardwareCoverage(IndirectCommandLayout Layout, GraphBufferId Arguments, int Width, int Height)
    {
        internal static void Record(ref RasterPassCommandScope commands, in HardwareCoverage data)
        {
            commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
            commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
            commands.ExecuteIndirect(data.Layout, new BufferRegion(commands.GetBuffer(data.Arguments), new BufferRange(72, 16)), 1);
        }
    }

    public void BindFrame(ref RenderGraphFrame graph, bool reuse)
    {
        graph.ObserveExecution(this);
        ReusedGeometry = reuse;
        foreach (GraphPassId pass in Builds) graph.SetPassEnabled(pass, !reuse);
        if (HardwareBinning)
            foreach (GraphPassId pass in CoveragePasses) graph.SetPassEnabled(pass, !reuse);
        if (!reuse) HasSubmittedGeometry = false;
    }

    public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!ReusedGeometry && result.TryGetPassCompletion(Builds[^1], out _))
            HasSubmittedGeometry = true;
    }

    public (ulong FixedBytes, ulong CapacityBytes, ulong AllocationBytes) GetMemoryUsage(
        ref RenderGraphEdit edit, IGraphicsBackend backend, Device device)
    {
        HashSet<GraphBufferId> variable = [Triangles, References, SortScratch, DepthReferences,
            ReferenceDepths, HardwareMasks, HardwareAssociations, ProjectedSources, Fragments];
        ulong fixedBytes = 0, capacityBytes = 0, allocationBytes = 0;
        foreach (GraphBufferId buffer in _buffers.Values.Select(value => value.Buffer).Where(value => value != default).Distinct())
        {
            BufferDesc description = edit.GetBufferDescription(buffer);
            capacityBytes += description.Size;
            allocationBytes += backend.GetBufferMemoryRequirements(device, description).Size;
            if (!variable.Contains(buffer)) fixedBytes += description.Size;
        }
        return (fixedBytes, capacityBytes, allocationBytes);
    }

    public uint GetFragmentCapacity(ref RenderGraphEdit edit) => _description.PixelLists
        ? checked((uint)(edit.GetBufferDescription(Fragments).Size / 16u)) : 0;

    public void Remove(ref RenderGraphEdit edit)
    {
        edit.Remove(Geometry); edit.Remove(Coverage); edit.Remove(Requests); edit.Remove(Color);
        foreach (GraphBufferId buffer in _buffers.Values.Select(value => value.Buffer).Where(value => value != default).Distinct())
            edit.Remove(buffer);
    }

    private sealed class PassResources(TileRendering tiles, string entry) : IGraphResourceProvider
    {
        public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
        {
            if (!tiles._buffers.TryGetValue(name, out var buffer)) { binder = null!; return false; }
            binder = (ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
                tiles.Bind(ref graph, ref pass, entry, name, type, buffer.Buffer, buffer.Stride, sync);
            return true;
        }
    }

    private void Bind(ref RenderGraphEdit graph, ref PassDefinition pass, string entry, string name,
        ResourceBindingType type, GraphBufferId buffer, uint stride, PipelineSync sync)
    {
        bool hardware = name is "TransparencyCoverageRanges" or "TransparencyHardwareMasks" or "TransparencyHardwareAssociations";
        bool usesHardware = entry switch
        {
            "CountTransparentCells" => name == "TransparencyCoverageRanges" || HardwareBinning,
            "ClearTransparentHardwareMasks" or "StoreTransparentCoverage" => true,
            "CountTransparentHardwareCells" or "ScatterTransparentHardwareCells" or
                "PrepareTransparentCells" or "SortTransparentCells" => HardwareBinning,
            "ResolveTileTransparentColor" or "ResolveLocalTileTransparentColor" or
                "ResolveDepthTileTransparentColor" => _description.IndexedColor,
            _ => false,
        };
        bool depth = name is "TransparencyDepthCells" or "TransparencyDepthReferences";
        if ((depth && !AdaptiveDepth) || (hardware && (!_description.HardwareCoverage || _description.PixelLists || !_description.Indexed || !usesHardware)))
        {
            pass.Bind(GraphParameterResourceBinding.Null(type));
            return;
        }
        BufferContentScope contentScope = hardware || name is "TransparencyRasterTriangles" or "TransparencyProjectedSources"
            ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange;
        if (type == ResourceBindingType.BufferSrv)
        {
            pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), sync, contentScope);
            return;
        }
        string accessEntry = entry is "PrepareTransparentCells" or "SortTransparentRanges" ? "SortTransparentCells" : entry;
        GraphAccessMode mode = (accessEntry, name) switch
        {
            ("ClearTransparentTiles", _) => GraphAccessMode.Write,
            ("CopyTransparentSources", "TransparencyTriangleCount") => GraphAccessMode.ReadWrite,
            ("CopyTransparentSources", "TransparencyArguments") => GraphAccessMode.ReadWrite,
            ("BuildTransparentGeometry", "TransparencyTriangles" or "TransparencyProjectedSources") => GraphAccessMode.Write,
            ("BuildTransparentGeometry", "TransparencyTriangleCount") => GraphAccessMode.ReadWrite,
            ("BuildTransparentGeometry", "TransparencyArguments") => GraphAccessMode.ReadWrite,
            ("CountTransparentCells", "TransparencyTileCounts") => GraphAccessMode.ReadWrite,
            ("CountTransparentCells", "TransparencyCellWork") => GraphAccessMode.ReadWrite,
            ("CountTransparentCells", "TransparencyCoverageRanges" or "TransparencyHardwareAssociations") => GraphAccessMode.Write,
            ("ReserveTransparentCells", "TransparencyCellWork") => GraphAccessMode.ReadWrite,
            ("ReserveTransparentCells", "TransparencyCellTrees") => GraphAccessMode.Write,
            ("ReserveTransparentCells", "TransparencyCellOffsets" or "TransparencyArguments") => GraphAccessMode.Write,
            ("ReserveTransparentCells", "TransparencyTriangleCount" or "TransparencyCellStreams") => GraphAccessMode.ReadWrite,
            ("ScatterTransparentCells", "TransparencyReferences") => GraphAccessMode.Write,
            ("ScatterTransparentCells", "TransparencyCellWrites") => GraphAccessMode.ReadWrite,
            ("CountTransparentHardwareCells", "TransparencyHardwareAssociations" or "TransparencyTileCounts" or "TransparencyCellWork") => GraphAccessMode.ReadWrite,
            ("SortTransparentCells", "TransparencyReferences") => GraphAccessMode.ReadWrite,
            ("SortTransparentCells", "TransparencyCellWork" or "TransparencyCellTrees" or "TransparencyTileWork") => GraphAccessMode.ReadWrite,
            ("SortTransparentCells", "TransparencySortJobs") => entry == "PrepareTransparentCells" ? GraphAccessMode.ReadWrite : GraphAccessMode.Read,
            ("SortTransparentCells", "TransparencyDepthReferences" or "TransparencyDepthCells") => entry == "SortTransparentRanges" ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            ("SortTransparentCells", "TransparencyReferenceDepths") => GraphAccessMode.Write,
            ("SortTransparentCells", "TransparencyTilePartitions") => GraphAccessMode.ReadWrite,
            ("ClassifyTransparentTiles", "TransparencyTileWork") => GraphAccessMode.ReadWrite,
            ("ClassifyTransparentTiles", "TransparencyDepthCells" or "TransparencyTilePartitions") => GraphAccessMode.ReadWrite,
            ("ScatterTransparentHardwareCells", "TransparencyReferences") => GraphAccessMode.Write,
            ("ScatterTransparentHardwareCells", "TransparencyCellWrites") => GraphAccessMode.ReadWrite,
            ("SortTransparentCells", "TransparencySortScratch" or "TransparencyDepthNodes") => GraphAccessMode.Write,
            ("FinalizeTransparentTiles", "TransparencyArguments") => GraphAccessMode.Write,
            ("FinalizeTransparentSort", "TransparencyArguments") => GraphAccessMode.Write,
            ("ClearTransparentHardwareMasks", "TransparencyHardwareMasks") => GraphAccessMode.Write,
            ("StoreTransparentCoverage", "TransparencyHardwareMasks" or "TransparencyHardwareAssociations" or "TransparencyTileCounts" or "TransparencyCellWork") => GraphAccessMode.ReadWrite,
#if SOMEENGINE_TRANSPARENCY_RESEARCH
            ("PrepareTransparentPixelLists", "TransparencyArguments" or "TransparencyPixelHeads" or "TransparencyFragmentCount") => GraphAccessMode.Write,
            ("StoreTransparencyReference", "TransparencyFragments") => GraphAccessMode.Write,
            ("StoreTransparencyReference", "TransparencyPixelHeads" or "TransparencyFragmentCount") => GraphAccessMode.ReadWrite,
            ("BuildTransparentPixelLists", "TransparencyFragments") => GraphAccessMode.Write,
            ("BuildTransparentPixelLists", "TransparencyPixelHeads" or "TransparencyFragmentCount") => GraphAccessMode.ReadWrite,
            ("ResolveTransparentPixelLists", "TransparencyFragments") => GraphAccessMode.ReadWrite,
#endif
            _ => GraphAccessMode.Read,
        };
        bool clear = entry == "ClearTransparentTiles" ||
            (entry == "PrepareTransparentPixelLists" && name is "TransparencyPixelHeads" or "TransparencyFragmentCount");
        pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), sync, mode,
            clear ? WriteCoverage.Complete : WriteCoverage.Partial, ResourceContentState.Defined, contentScope);
    }
}
