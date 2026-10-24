using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using Buffer = SomeEngine.Graphics.Buffer;
using Shader = SomeEngine.Resources.Shader;

namespace SomeEngine.Transparency.Research;

public enum TransparencyComparison { Weighted, WeightedCubic, FourMoments, SixMoments, PixelSort, TileHardware, TileSoftware }

public sealed class RasterTransparencyRenderer : ITransparencyRenderer
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly TransparencyComparison _method;
    private readonly List<IDisposable> _resources = [];
    private readonly ComparisonShaders _shaders;
    private readonly List<(Buffer Upload, Buffer Destination)> _uploads = [];
    private readonly Dictionary<string, ResourceBinding> _bindings = [];
    private readonly Texture _sceneColor, _depth;
    private readonly DepthStencilView? _hardwareDepth;
    private readonly Buffer? _vertices;
    private readonly Buffer _colors, _uniforms;
    private readonly Buffer? _heads, _counters, _fragments;
    private readonly Buffer? _coverageMasks;
    private readonly uint _associationCount;
    private readonly Target? _accumulation, _revealage, _absorbance, _moments, _extraMoments;
    private readonly Drawing? _draw;
    private readonly Drawing? _shade;
    private readonly Computing _resolve;
    private readonly Computing _background;
    private readonly Computing? _clearHeads;
    private readonly CommandContext _context;
    private readonly QueryPool _queries;
    private readonly Buffer _queryReadback;
    private readonly ulong _frequency;
    private readonly uint _vertexCount;
    private bool _rendered;

    public RasterTransparencyRenderer(IGraphicsBackend backend, Device device, int width, int height,
        TriangleHierarchy geometry, TransparencyComparison method, ComparisonShaders? shaders = null,
        string tileResolveFile = "tile_visibility_resolve.slang")
    {
        (_backend, _device, Width, Height, _method) = (backend, device, width, height, method);
        _shaders = shaders ?? new ComparisonShaders();
        _frequency = backend.CalibrateTimestamps(backend.GetQueue(device, QueueType.Graphics)).QueueFrequency;
        _context = Keep(backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1)));
        _queries = Keep(backend.CreateQueryPool(device, new(QueryType.Timestamp, QueueType.Graphics, 4)));
        _queryReadback = Keep(backend.CreateBuffer(device, new(32, BufferUsages.QueryResolve), MemoryType.Readback));
        var vertices = geometry.Triangles.Select((triangle, index) =>
            new ForwardTriangle(triangle.A, triangle.B, triangle.C,
                method is TransparencyComparison.TileHardware or TransparencyComparison.TileSoftware ? (uint)index : triangle.TriangleId,
                geometry.Coverage[index].ColorIndex)).ToArray();
        var random = new Random(314159);
        for (int index = vertices.Length - 1; index > 0; index--)
        {
            int other = random.Next(index + 1);
            (vertices[index], vertices[other]) = (vertices[other], vertices[index]);
        }
        _vertexCount = (uint)vertices.Length * 3;
        if (method != TransparencyComparison.TileSoftware) _vertices = Upload(vertices.AsSpan());
        _colors = Upload(geometry.Colors.AsSpan());
        GeometryBytes = (long)((_vertices?.Info.Size ?? 0) + _colors.Info.Size);
        _uniforms = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload));
        using (MappedBuffer mapping = backend.Map(_uniforms, MapType.Write, BufferRange.Whole))
        {
            Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            values[0] = (uint)width; values[1] = (uint)height;
            values[2] = BitConverter.SingleToUInt32Bits(method == TransparencyComparison.SixMoments ? 5e-6f : 5e-7f);
            values[3] = BitConverter.SingleToUInt32Bits(.25f);
            mapping.Flush(mapping.Range);
        }
        _sceneColor = CreateTexture(Format.R16G16B16A16Float,
            TextureUsages.Storage | TextureUsages.CopySource);
        bool tileHardware = method == TransparencyComparison.TileHardware;
        _depth = CreateTexture(tileHardware ? Format.D32Float : Format.R32Float,
            TextureUsages.Sampled | TextureUsages.CopyDestination |
            (tileHardware ? TextureUsages.DepthStencilAttachment : TextureUsages.ColorAttachment));
        if (tileHardware)
            _hardwareDepth = Keep(backend.CreateDepthStencilView(device, new(_depth, new(0, 1, 0, 1, TextureAspects.Depth),
                Format.D32Float, TextureViewDimension.Texture2D, ReadOnlyDepth: true)));
        _bindings["Settings"] = ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(_uniforms, BufferRange.Whole))));
        if (_vertices is { } vertexBuffer) _bindings["Vertices"] = ReadBuffer(vertexBuffer, 56);
        _bindings["Colors"] = ReadBuffer(_colors, 16);
        _bindings["SceneColor"] = ResourceBinding.StorageTexture(Keep(backend.CreateTextureUav(device,
            new(_sceneColor, ColorRange, Format.R16G16B16A16Float, TextureViewDimension.Texture2DArray))));
        if (!tileHardware)
            _bindings["OpaqueDepth"] = ResourceBinding.SampledTexture(Keep(backend.CreateTextureSrv(device,
                new(_depth, ColorRange, Format.R32Float, TextureViewDimension.Texture2DArray))));
        _background = Compute("comparison_background.slang", "FillBackground");

        if (method is TransparencyComparison.TileHardware or TransparencyComparison.TileSoftware)
        {
            var tiles = new TileVisibilityGeometry(geometry, width, height);
            Buffer triangleData = Upload(tiles.Triangles.AsSpan());
            Buffer associations = Upload(tiles.Associations.AsSpan());
            Buffer ranges = Upload(tiles.Ranges.AsSpan());
            _associationCount = (uint)tiles.Associations.Length;
            _bindings["TileTriangles"] = ReadBuffer(triangleData, 32);
            _bindings["TileAssociations"] = ReadBuffer(associations, 4);
            _bindings["TileRanges"] = ReadBuffer(ranges, 8);
            GeometryBytes += (long)(triangleData.Info.Size + associations.Info.Size + ranges.Info.Size);
            if (method == TransparencyComparison.TileSoftware)
            {
                Buffer coverage = Upload(geometry.Coverage.AsSpan());
                _bindings["Coverage"] = ReadBuffer(coverage, 56);
                GeometryBytes += (long)coverage.Info.Size;
            }
            if (method == TransparencyComparison.TileHardware)
            {
                _coverageMasks = Keep(backend.CreateBuffer(device, new((ulong)_associationCount * 32, BufferUsages.ShaderWrite)));
                _bindings["CoverageMasks"] = WriteBuffer(_coverageMasks, 4);
                IntermediateBytes = (long)_coverageMasks.Info.Size;
                _clearHeads = Compute("tile_visibility_coverage.slang", "ClearTileCoverage");
                _draw = Draw("tile_visibility_draw.slang", "StoreTileCoverage", [], []);
            }
            using (MappedBuffer mapping = backend.Map(_uniforms, MapType.Write, BufferRange.Whole))
            {
                Span<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
                values[4] = (uint)tiles.TileColumns; values[5] = (uint)tiles.TileRows;
                values[6] = _associationCount; values[7] = BitConverter.SingleToUInt32Bits(1f / 255);
                mapping.Flush(mapping.Range);
            }
            _resolve = Compute(tileResolveFile, "ResolveTileVisibility",
                method == TransparencyComparison.TileSoftware ? "#define TILE_SOFTWARE_RASTER 1\n" : "");
            Initialize();
        }
        else if (method == TransparencyComparison.PixelSort)
        {
            _heads = Keep(backend.CreateBuffer(device, new((ulong)width * (ulong)height * 4, BufferUsages.ShaderWrite)));
            _counters = Keep(backend.CreateBuffer(device, new(8, BufferUsages.ShaderWrite | BufferUsages.CopySource)));
            _bindings["Heads"] = WriteBuffer(_heads, 4);
            _bindings["Counters"] = WriteBuffer(_counters, 4);
            Initialize();
            (FragmentCount, MaximumFragmentsPerPixel) = CountCoverage();
            _fragments = Keep(backend.CreateBuffer(device, new(FragmentCount * 16, BufferUsages.ShaderWrite)));
            _bindings["Fragments"] = WriteBuffer(_fragments, 16);
            IntermediateBytes = (long)(_heads.Info.Size + _counters.Info.Size + _fragments.Info.Size);
            _clearHeads = Compute("sorted_clear.slang", "ClearHeads");
            _draw = Draw("sorted_draw.slang", "StoreFragments", [], []);
            _resolve = Compute("sorted_resolve.slang", "ResolveSorted",
                "#define SORTED_FRAGMENT_COUNT " + MaximumFragmentsPerPixel + "\n" +
                (MaximumFragmentsPerPixel > 32 ? "#define SORT_LINKED_MERGE 1\n" : ""));
        }
        else if (method is TransparencyComparison.FourMoments or TransparencyComparison.SixMoments)
        {
            _absorbance = CreateTarget(Format.R32Float, 4);
            _moments = CreateTarget(Format.R32G32B32A32Float, 16);
            _accumulation = CreateTarget(Format.R16G16B16A16Float, 8);
            _bindings["Absorbance"] = _absorbance.Binding;
            _bindings["Moments"] = _moments.Binding;
            _bindings["Accumulation"] = _accumulation.Binding;
            BlendAttachmentState additive = new(Enabled: true, DestinationColor: BlendFactor.One, DestinationAlpha: BlendFactor.One);
            string definitions = method == TransparencyComparison.SixMoments ? "#define SIX_POWER_MOMENTS 1\n" : "";
            if (method == TransparencyComparison.SixMoments)
            {
                _extraMoments = CreateTarget(Format.R32G32Float, 8);
                _bindings["ExtraMoments"] = _extraMoments.Binding;
            }
            _draw = Draw("moments_draw.slang", "DrawMoments", _extraMoments is null
                ? [Format.R32Float, Format.R32G32B32A32Float] : [Format.R32Float, Format.R32G32B32A32Float, Format.R32G32Float],
                _extraMoments is null ? [additive, additive] : [additive, additive, additive], definitions);
            _shade = Draw("moments_shade.slang", "ShadeMoments", [Format.R16G16B16A16Float], [additive], definitions);
            _resolve = Compute("moments_resolve.slang", "ResolveMoments");
            Initialize();
        }
        else
        {
            _accumulation = CreateTarget(Format.R16G16B16A16Float, 8);
            _revealage = CreateTarget(Format.R16Float, 2);
            _bindings["Accumulation"] = _accumulation.Binding;
            _bindings["Revealage"] = _revealage.Binding;
            BlendAttachmentState additive = new(Enabled: true, DestinationColor: BlendFactor.One, DestinationAlpha: BlendFactor.One);
            BlendAttachmentState reveal = new(Enabled: true, SourceColor: BlendFactor.Zero,
                DestinationColor: BlendFactor.OneMinusSourceColor, SourceAlpha: BlendFactor.Zero,
                DestinationAlpha: BlendFactor.One, WriteMask: ColorWriteMasks.Red);
            _draw = Draw("weighted_draw.slang", "DrawWeighted", [Format.R16G16B16A16Float, Format.R16Float],
                [additive, reveal], method == TransparencyComparison.WeightedCubic ? "#define CUBIC_DEPTH_WEIGHT 1\n" : "");
            _resolve = Compute("weighted_resolve.slang", "ResolveWeighted");
            Initialize();
        }
    }

    public int Width { get; }
    public int Height { get; }
    public long GeometryBytes { get; }
    public long IntermediateBytes { get; private set; }
    public ulong FragmentCount { get; }
    public uint MaximumFragmentsPerPixel { get; }
    public double FirstPassMs { get; private set; }
    public double SecondPassMs { get; private set; }
    public double ResolveMs { get; private set; }

    public double Render()
    {
        _backend.Begin(_context);
        _backend.ResetQueries(_context, _queries, 0, 4);
        Record(_queries, 0);
        _backend.ResolveQueries(_context, _queries, 0, 4, _queryReadback, BufferRange.Whole);
        Submit();
        using MappedBuffer mapping = _backend.Map(_queryReadback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> values = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        FirstPassMs = (values[1] - values[0]) * 1000.0 / _frequency;
        SecondPassMs = (values[2] - values[1]) * 1000.0 / _frequency;
        ResolveMs = (values[3] - values[2]) * 1000.0 / _frequency;
        return (values[3] - values[0]) * 1000.0 / _frequency;
    }

    public TransparencyTimes Measure(int warmupFrames, int samples)
    {
        using QueryPool queries = _backend.CreateQueryPool(_device, new(QueryType.Timestamp, QueueType.Graphics, (uint)samples * 4));
        using Buffer readback = _backend.CreateBuffer(_device, new((ulong)samples * 32, BufferUsages.QueryResolve), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.ResetQueries(_context, queries, 0, (uint)samples * 4);
        for (int frame = 0; frame < warmupFrames; frame++) Record(null, 0);
        for (int frame = 0; frame < samples; frame++) Record(queries, (uint)frame * 4);
        _backend.ResolveQueries(_context, queries, 0, (uint)samples * 4, readback, BufferRange.Whole);
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<ulong> values = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
        var times = new double[samples];
        var firstPass = new double[samples];
        var secondPass = new double[samples];
        var resolves = new double[samples];
        for (int frame = 0; frame < samples; frame++)
        {
            int query = frame * 4;
            firstPass[frame] = (values[query + 1] - values[query]) * 1000.0 / _frequency;
            secondPass[frame] = (values[query + 2] - values[query + 1]) * 1000.0 / _frequency;
            resolves[frame] = (values[query + 3] - values[query + 2]) * 1000.0 / _frequency;
            times[frame] = (values[query + 3] - values[query]) * 1000.0 / _frequency;
        }
        return new(times, firstPass, secondPass, resolves);
    }

    private void Record(QueryPool? queries, uint firstQuery)
    {
        _backend.Barrier(_context, new TextureBarrier(_sceneColor, ColorRange,
            _rendered ? PipelineSync.ComputeShading : PipelineSync.None, PipelineSync.ComputeShading,
            _rendered ? ResourceAccess.UnorderedAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            _rendered ? TextureLayout.UnorderedAccess : TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        Run(_background, linear: false);
        _backend.Barrier(_context, new TextureBarrier(_sceneColor, ColorRange, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess, TextureLayout.UnorderedAccess, TextureLayout.UnorderedAccess));
        Timestamp(queries, firstQuery);
        if (_method == TransparencyComparison.TileSoftware)
        {
            Timestamp(queries, firstQuery + 1);
        }
        else if (_method == TransparencyComparison.TileHardware)
        {
            _backend.Barrier(_context, new BufferBarrier(_coverageMasks!,
                _rendered ? PipelineSync.ComputeShading : PipelineSync.None,
                PipelineSync.ComputeShading,
                _rendered ? ResourceAccess.UnorderedAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            Run(_clearHeads!, new DispatchArguments((_associationCount * 8 + 255) / 256, 1, 1));
            UavBarrier(_coverageMasks!, PipelineSync.ComputeShading, PipelineSync.PixelShading);
            Run(_draw!, []);
            Timestamp(queries, firstQuery + 1);
            UavBarrier(_coverageMasks!, PipelineSync.PixelShading, PipelineSync.ComputeShading);
        }
        else if (_method == TransparencyComparison.PixelSort)
        {
            UavBarrier(_heads!, PipelineSync.PixelShading | PipelineSync.ComputeShading, PipelineSync.ComputeShading);
            UavBarrier(_counters!, PipelineSync.PixelShading | PipelineSync.ComputeShading, PipelineSync.ComputeShading);
            Run(_clearHeads!, linear: true);
            UavBarrier(_heads!, PipelineSync.ComputeShading, PipelineSync.PixelShading);
            UavBarrier(_counters!, PipelineSync.ComputeShading, PipelineSync.PixelShading);
            _backend.Barrier(_context, new BufferBarrier(_fragments!,
                _rendered ? PipelineSync.ComputeShading : PipelineSync.None, PipelineSync.PixelShading,
                _rendered ? ResourceAccess.UnorderedAccess : ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            Run(_draw!, []);
            Timestamp(queries, firstQuery + 1);
            UavBarrier(_heads!, PipelineSync.PixelShading, PipelineSync.ComputeShading);
            UavBarrier(_fragments!, PipelineSync.PixelShading, PipelineSync.ComputeShading);
        }
        else if (_method is TransparencyComparison.FourMoments or TransparencyComparison.SixMoments)
        {
            ToTarget(_absorbance!); ToTarget(_moments!);
            if (_extraMoments is not null) ToTarget(_extraMoments);
            Run(_draw!, _extraMoments is null
                ? [_absorbance!.Attachment(Vector4.Zero), _moments!.Attachment(Vector4.Zero)]
                : [_absorbance!.Attachment(Vector4.Zero), _moments!.Attachment(Vector4.Zero), _extraMoments.Attachment(Vector4.Zero)]);
            Timestamp(queries, firstQuery + 1);
            ToRead(_absorbance!, PipelineSync.PixelShading | PipelineSync.ComputeShading);
            ToRead(_moments!, PipelineSync.PixelShading);
            if (_extraMoments is not null) ToRead(_extraMoments, PipelineSync.PixelShading);
            ToTarget(_accumulation!);
            Run(_shade!, [_accumulation!.Attachment(Vector4.Zero)]);
            ToRead(_accumulation!, PipelineSync.ComputeShading);
        }
        else
        {
            ToTarget(_accumulation!); ToTarget(_revealage!);
            Run(_draw!, [_accumulation!.Attachment(Vector4.Zero), _revealage!.Attachment(Vector4.One)]);
            Timestamp(queries, firstQuery + 1);
            ToRead(_accumulation!, PipelineSync.ComputeShading);
            ToRead(_revealage!, PipelineSync.ComputeShading);
        }
        Timestamp(queries, firstQuery + 2);
        if (_method is TransparencyComparison.TileHardware or TransparencyComparison.TileSoftware)
            Run(_resolve, new DispatchArguments((uint)(Width + 15) / 16, (uint)(Height + 15) / 16, 1));
        else Run(_resolve, linear: false);
        Timestamp(queries, firstQuery + 3);
        _rendered = true;
    }

    private void Timestamp(QueryPool? queries, uint index)
    {
        if (queries is not null) _backend.WriteTimestamp(_context, queries, index);
    }

    public Vector4[] ReadColors()
    {
        uint pitch = ((uint)Width * 8 + 255) & ~255u;
        using Buffer readback = _backend.CreateBuffer(_device, new((ulong)pitch * (uint)Height, BufferUsages.CopyDestination), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.Barrier(_context, new TextureBarrier(_sceneColor, ColorRange, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource, TextureLayout.UnorderedAccess, TextureLayout.CopySource));
        _backend.CopyTextureToBuffer(_context, new(readback, 0, pitch, (uint)Height, _sceneColor, 0, 0, TextureAspects.Color,
            0, 0, 0, (uint)Width, (uint)Height, 1));
        _backend.Barrier(_context, new TextureBarrier(_sceneColor, ColorRange, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess, TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        var colors = new Vector4[Width * Height];
        for (int row = 0; row < Height; row++)
        {
            ReadOnlySpan<Half> values = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(row * (int)pitch, Width * 8));
            for (int column = 0; column < Width; column++)
                colors[row * Width + column] = new((float)values[column * 4], (float)values[column * 4 + 1],
                    (float)values[column * 4 + 2], (float)values[column * 4 + 3]);
        }
        return colors;
    }

    private (ulong Total, uint Maximum) CountCoverage()
    {
        Computing clear = Compute("sorted_clear.slang", "ClearCounts");
        Computing maximum = Compute("sorted_clear.slang", "FindMaximumCount");
        Drawing count = Draw("sorted_count.slang", "CountFragments", [], []);
        using Buffer readback = _backend.CreateBuffer(_device, new(8, BufferUsages.CopyDestination), MemoryType.Readback);
        _backend.Begin(_context);
        _backend.Barrier(_context, new BufferBarrier(_heads!, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        _backend.Barrier(_context, new BufferBarrier(_counters!, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        Run(clear, linear: true);
        UavBarrier(_heads!, PipelineSync.ComputeShading, PipelineSync.PixelShading);
        UavBarrier(_counters!, PipelineSync.ComputeShading, PipelineSync.PixelShading);
        Run(count, []);
        UavBarrier(_heads!, PipelineSync.PixelShading, PipelineSync.ComputeShading);
        UavBarrier(_counters!, PipelineSync.PixelShading, PipelineSync.ComputeShading);
        Run(maximum, linear: true);
        _backend.Barrier(_context, new BufferBarrier(_counters!, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        _backend.CopyBuffer(_context, new(_counters!, 0, readback, 0, 8));
        _backend.Barrier(_context, new BufferBarrier(_counters!, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
        Submit();
        using MappedBuffer mapping = _backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        ReadOnlySpan<uint> values = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
        return (values[0], values[1]);
    }

    private void Initialize()
    {
        _backend.Begin(_context);
        foreach (var copy in _uploads)
        {
            _backend.Barrier(_context, new BufferBarrier(copy.Destination, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            _backend.CopyBuffer(_context, new(copy.Upload, 0, copy.Destination, 0, copy.Destination.Info.Size));
            _backend.Barrier(_context, new BufferBarrier(copy.Destination, PipelineSync.Copy,
                PipelineSync.VertexShading | PipelineSync.PixelShading | PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
        }
        if (_hardwareDepth is not null)
        {
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Depth);
            using DepthStencilView view = _backend.CreateDepthStencilView(_device,
                new(_depth, range, Format.D32Float, TextureViewDimension.Texture2D));
            _backend.Barrier(_context, new TextureBarrier(_depth, range, PipelineSync.None, PipelineSync.DepthStencil,
                ResourceAccess.NoAccess, ResourceAccess.DepthStencilWrite, TextureLayout.Undefined, TextureLayout.DepthStencilWrite));
            _backend.BeginRendering(_context, new([], new(view, LoadType.Clear, StoreType.Store, LoadType.Discard, StoreType.Discard, 0),
                (uint)Width, (uint)Height));
            _backend.EndRendering(_context);
            _backend.Barrier(_context, new TextureBarrier(_depth, range, PipelineSync.DepthStencil, PipelineSync.DepthStencil,
                ResourceAccess.DepthStencilWrite, ResourceAccess.DepthStencilRead, TextureLayout.DepthStencilWrite, TextureLayout.DepthStencilRead));
            Submit();
        }
        else
        {
            _backend.Barrier(_context, new TextureBarrier(_depth, ColorRange, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
            _backend.ClearTexture(_context, _depth, ColorRange, Vector4.Zero);
            _backend.Barrier(_context, new TextureBarrier(_depth, ColorRange, PipelineSync.Copy,
                PipelineSync.PixelShading | PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            Submit();
        }
        foreach (var copy in _uploads) copy.Upload.Dispose();
        _uploads.Clear();
    }

    private Drawing Draw(string file, string entry, Format[] formats, BlendAttachmentState[] blends, string definitions = "")
    {
        Shader shader = _shaders.Get(file, ["DrawTransparency", entry], definitions);
        int target = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection vertex = shader.RequireSlangEntry(target, "DrawTransparency", SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(target, entry, SlangStage.Fragment);
        Pipeline pipeline = Keep(_backend.CreateGraphicsPipeline(_device, new(shader.SlangCode, reflection,
            vertex, shader.GetSlangEntryBytes(target, vertex), pixel, shader.GetSlangEntryBytes(target, pixel),
            [], [], PrimitiveTopology.TriangleList, StripCut.Disabled, new RasterizerState(Cull: CullType.None),
            new MultisampleState(SampleCount: 1),
            _hardwareDepth is null ? new DepthStencilState() : new DepthStencilState(DepthTest: true, DepthComparison: CompareOperation.GreaterOrEqual),
            new BlendState(blends, independentBlend: true),
            new AttachmentFormatSignature(formats, _hardwareDepth is null ? null : Format.D32Float), DynamicStates.Viewport | DynamicStates.Scissor,
            vertexCodeBlob: shader.GetSlangEntryBlob(target, vertex), pixelCodeBlob: shader.GetSlangEntryBlob(target, pixel))));
        return new(pipeline, reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            GpuTransparencyRenderer.Bindings(reflection, _bindings));
    }

    private Computing Compute(string file, string entry, string definitions = "")
    {
        Shader shader = _shaders.Get(file, [entry], definitions);
        LinkedComputePipeline pipeline = Keep(LinkedComputePipeline.Create(_backend, _device, shader, entry, entry));
        return new(pipeline, GpuTransparencyRenderer.Bindings(pipeline.Reflection, _bindings));
    }

    private void Run(Drawing draw, ColorAttachmentDesc[] colors)
    {
        _backend.SetPipeline(_context, draw.Pipeline);
        _backend.SetViewports(_context, [new(0, 0, Width, Height)]);
        _backend.SetScissors(_context, [new(0, 0, Width, Height)]);
        _backend.SetTransientParameterBindings(_context, new(draw.Layout, draw.Bindings, []));
        _backend.BeginRendering(_context, new(colors, _hardwareDepth is null ? null :
            new(_hardwareDepth, LoadType.Load, StoreType.Store, LoadType.Discard, StoreType.Discard), (uint)Width, (uint)Height));
        _backend.Draw(_context, new(_vertexCount, 1, 0, 0));
        _backend.EndRendering(_context);
    }

    private void Run(Computing compute, bool linear)
        => Run(compute, linear
            ? new DispatchArguments((uint)(Width * Height + 255) / 256, 1, 1)
            : new DispatchArguments((uint)(Width + 7) / 8, (uint)(Height + 7) / 8, 1));

    private void Run(Computing compute, DispatchArguments dispatch)
    {
        _backend.SetPipeline(_context, compute.Pipeline.Pipeline);
        _backend.SetTransientParameterBindings(_context, new(compute.Pipeline.ParameterLayout, compute.Bindings, []));
        _backend.Dispatch(_context, dispatch);
    }

    private void ToTarget(Target target) => _backend.Barrier(_context, new TextureBarrier(target.Texture, ColorRange,
        _rendered ? PipelineSync.PixelShading | PipelineSync.ComputeShading : PipelineSync.None, PipelineSync.RenderTarget,
        _rendered ? ResourceAccess.ShaderResource : ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
        _rendered ? TextureLayout.ShaderResource : TextureLayout.Undefined, TextureLayout.RenderTarget));

    private void ToRead(Target target, PipelineSync shading) => _backend.Barrier(_context,
        new TextureBarrier(target.Texture, ColorRange, PipelineSync.RenderTarget, shading,
            ResourceAccess.RenderTarget, ResourceAccess.ShaderResource, TextureLayout.RenderTarget, TextureLayout.ShaderResource));

    private void UavBarrier(Buffer buffer, PipelineSync before, PipelineSync after) => _backend.Barrier(_context,
        new BufferBarrier(buffer, before, after, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));

    private Target CreateTarget(Format format, int pixelBytes)
    {
        Texture texture = CreateTexture(format, TextureUsages.Sampled | TextureUsages.ColorAttachment);
        ColorAttachmentView view = Keep(_backend.CreateColorAttachmentView(_device, new(texture, ColorRange, format, TextureViewDimension.Texture2D)));
        ResourceBinding binding = ResourceBinding.SampledTexture(Keep(_backend.CreateTextureSrv(_device,
            new(texture, ColorRange, format, TextureViewDimension.Texture2D))));
        IntermediateBytes += (long)Width * Height * pixelBytes;
        return new(texture, view, binding);
    }

    private Texture CreateTexture(Format format, TextureUsages usages) => Keep(_backend.CreateTexture(_device,
        new(TextureDimension.Texture2D, (uint)Width, (uint)Height, 1, 1, 1, 1, format, usages)));

    private Buffer Upload<T>(Span<T> values) where T : unmanaged
    {
        ulong bytes = (ulong)values.Length * (ulong)Marshal.SizeOf<T>();
        Buffer upload = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource), MemoryType.Upload);
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new(bytes, BufferUsages.ShaderRead | BufferUsages.CopyDestination), MemoryType.DeviceLocal));
        using (MappedBuffer mapping = _backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        _uploads.Add((upload, buffer));
        return buffer;
    }

    private ResourceBinding ReadBuffer(Buffer buffer, uint stride) =>
        ResourceBinding.ReadOnlyBuffer(Keep(_backend.CreateBufferSrv(_device, new(buffer, BufferRange.Whole, StructureStride: stride))));
    private ResourceBinding WriteBuffer(Buffer buffer, uint stride) =>
        ResourceBinding.WritableBuffer(Keep(_backend.CreateBufferUav(_device, new(buffer, BufferRange.Whole, StructureStride: stride))));

    private void Submit()
    {
        using RecordedCommands commands = _backend.End(_context);
        QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Graphics), new([], [], [commands], [], []));
        if (_backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
            throw new TimeoutException("Transparency comparison timed out.");
    }

    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    public void Dispose() { for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ForwardTriangle(Vector4 A, Vector4 B, Vector4 C, uint TriangleId, uint ColorIndex);
    private sealed record Drawing(Pipeline Pipeline, VariableLayoutReflection Layout, ResourceBinding[] Bindings);
    private sealed record Computing(LinkedComputePipeline Pipeline, ResourceBinding[] Bindings);
    private sealed record Target(Texture Texture, ColorAttachmentView View, ResourceBinding Binding)
    {
        public ColorAttachmentDesc Attachment(Vector4 clear) => new(View, LoadType.Clear, StoreType.Store, clear);
    }
}
