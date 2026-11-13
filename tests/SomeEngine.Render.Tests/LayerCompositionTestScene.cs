using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Features.Layers;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources.Layers;
using SomeEngine.Assets.Importers;
using SomeEngine.Render;

namespace SomeEngine.Render.Tests;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal readonly record struct LayerTestFragment(uint Tile, uint Pixel, float Depth, uint Identity,
    uint Material = 0, uint DrawOrder = 0, uint Pass = 0, uint Reserved = 1);

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
internal readonly record struct LayerTestMaterial(Vector4 PremultipliedColor, Vector4 Transmittance)
{
    internal static LayerTestMaterial Over(Vector3 color, float alpha) => new(new(color * alpha, 0), new(new Vector3(1 - alpha), 0));
    internal Vector3 Color => new((float)(Half)PremultipliedColor.X, (float)(Half)PremultipliedColor.Y, (float)(Half)PremultipliedColor.Z);
    internal Vector3 Transmission => new((float)(Half)Transmittance.X, (float)(Half)Transmittance.Y, (float)(Half)Transmittance.Z);
}

/// <summary>A public composition graph with small visibility and color-producing compute stages.</summary>
// #128: complete composition exercises complex shader control flow and uses the hardware test adapter.
internal sealed class LayerCompositionTestScene : IDisposable
{
    private readonly BinningTestDevice _graphics = new(adapterType: AdapterType.Discrete);
    private readonly RenderWorld _world = new();
    private readonly RenderExtractionSystems _extraction;
    private readonly ShaderLibrary _shaders;
    private readonly Shader _producer;
    private readonly LinkedComputePipeline _emit, _shade, _readColor;
    private readonly GraphBufferInput _sources, _palette;
    private readonly GraphBufferSrvId _sourcesView, _paletteView;
    private readonly GraphBufferId _dimensions, _colors;
    private readonly GraphBufferCbvId _dimensionsView;
    private readonly GraphBufferUavId _colorsView;
    private readonly GraphTextureSrvId _colorView;
    private readonly GraphPassId _clearInputs, _colorCopy;
    private readonly GraphReadbackId _colorReadback;
    private readonly ColorReceiver _result = new();
    private readonly GraphTextureId _opaqueColor, _opaqueDepth;
    private readonly uint _width, _height;
    private readonly byte[] _pageBytes;
    private readonly RenderGeometryResources _geometry;
    private readonly GraphBufferId _geometryPageHeap, _geometryVisible, _geometryInstances, _geometryProperties, _geometryViews;
    private readonly GraphBufferInput _geometryPropertiesInput;
    private readonly GraphBufferSrvId _geometryVisibleView, _geometryInstancesView, _geometryPageView;
    private readonly GraphBufferCbvId _geometryPropertiesView;
    private readonly GraphBufferUavId _geometryVisibleOutput, _geometryInstancesOutput, _geometryPropertiesOutput, _geometryViewsOutput;
    private readonly GraphPassId _initializeGeometry;

    internal global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
    internal LayerComposition Composition { get; }
    internal RenderPrepareSystems Prepare { get; }
    internal LayerPassId FirstPass { get; }
    internal Vector3[] Colors => _result.Values.Select(color => new Vector3(color.X, color.Y, color.Z)).ToArray();

    internal LayerCompositionTestScene(uint width = 1, uint height = 1, uint tileSize = 16,
        uint blockSize = 128, uint blockCapacity = 64, LayerSortMode sortMode = LayerSortMode.DepthFrontToBack,
        LayerAccumulator accumulator = LayerAccumulator.Color, Vector3 background = default)
    {
        (_width, _height) = (width, height);
        _extraction = new(_world);
        Prepare = new(_world, _extraction);
        string directory = ShaderDirectory();
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(global);
        settings.SearchPaths = [directory];
        _shaders = new(global, settings);
        Shader store = Load("Layer store", "layer_fragment_store.slang");
        Shader composite = Load("Layer composition", "layer_composite.slang");
        _producer = _shaders.CreateAsync("Layer composition behavior", "layer_composition_checks.slang", ProducerSource)
            .AsTask().GetAwaiter().GetResult();
        _emit = LinkedComputePipeline.Create(_graphics.Pipelines, _producer, "EmitLayerRecords", "Emit layer records");
        _shade = LinkedComputePipeline.Create(_graphics.Pipelines, _producer, "ShadeLayerRecords", "Shade layer records");
        _readColor = LinkedComputePipeline.Create(_graphics.Pipelines, _producer, "ReadLayerColors", "Read composed layer colors");
        LinkedComputePipeline initializeGeometry = LinkedComputePipeline.Create(_graphics.Pipelines, _producer,
            "InitializeLayerGeometry", "Initialize layer geometry");
        using (MeshAsset meshAsset = ClusterBuilder.ProcessRaw([new(-1,-1,.5f),new(1,-1,.5f),new(0,1,.5f)], [],
                   [0u,1u,2u], [new(Guid.Empty)], "Layer behavior triangle"))
        {
            Mesh mesh = meshAsset.GetBlock<ClusterMeshBlock>().CreateAsync(meshAsset.GetBlock<MeshBlock>(),
                "Layer behavior triangle").AsTask().GetAwaiter().GetResult();
            _pageBytes = new byte[mesh.GetPageByteLength(0)];
            mesh.ReadPage(0, _pageBytes);
        }
        Graph = new(_graphics.Backend, _graphics.Device, [_graphics.Backend.GetQueue(_graphics.Device, QueueType.Graphics)],
            new RenderGraphDesc(MaximumFramesInFlight: 2));
        RenderGraphEdit edit = Graph.BeginEdit();
        try
        {
            _sources = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _palette = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _sourcesView = edit.CreateBufferSrv(_sources, structureStride: 32);
            _paletteView = edit.CreateBufferSrv(_palette, structureStride: 32);
            const uint visibleCapacity = 8192;
            const BufferUsages geometryUses = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination;
            _geometryPageHeap = edit.CreatePersistentBuffer(new((ulong)_pageBytes.Length, geometryUses));
            edit.AddBufferUploadPass(_geometryPageHeap);
            _geometryVisible = edit.CreatePersistentBuffer(new(visibleCapacity * 16u, geometryUses));
            _geometryInstances = edit.CreatePersistentBuffer(new(visibleCapacity * 4u, geometryUses));
            _geometryProperties = edit.CreatePersistentBuffer(new(256u, geometryUses | BufferUsages.Constant));
            _geometryViews = edit.CreatePersistentBuffer(new(RenderRasterContext.ViewSizeInBytes, geometryUses));
            _geometryPropertiesInput = edit.CreateBufferInput(BufferUsages.Constant);
            _geometryVisibleView = edit.CreateBufferSrv(_geometryVisible, structureStride: 16);
            _geometryInstancesView = edit.CreateBufferSrv(_geometryInstances);
            _geometryPageView = edit.CreateBufferSrv(_geometryPageHeap);
            _geometryPropertiesView = edit.CreateBufferCbv(_geometryProperties);
            _geometryVisibleOutput = edit.CreateBufferUav(_geometryVisible, structureStride: 16);
            _geometryInstancesOutput = edit.CreateBufferUav(_geometryInstances);
            _geometryPropertiesOutput = edit.CreateBufferUav(_geometryProperties);
            _geometryViewsOutput = edit.CreateBufferUav(_geometryViews, structureStride: RenderRasterContext.ViewSizeInBytes);
            _initializeGeometry = Add(ref edit, initializeGeometry, "Initialize layer behavior geometry", default, new(1,1,1),
                (name, _) => name switch
                {
                    "LayerTestFragments" => GraphParameterResourceBinding.ReadOnlyBuffer(_sourcesView, PipelineSync.ComputeShading),
                    "LayerTestVisibleOutput" => GraphParameterResourceBinding.WritableBuffer(_geometryVisibleOutput,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined,
                        BufferContentScope.CountBoundedPrefix),
                    "LayerTestInstanceOutput" => GraphParameterResourceBinding.WritableBuffer(_geometryInstancesOutput,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined,
                        BufferContentScope.CountBoundedRanges),
                    "LayerTestPropertiesOutput" => GraphParameterResourceBinding.WritableBuffer(_geometryPropertiesOutput,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    "LayerTestViewsOutput" => GraphParameterResourceBinding.WritableBuffer(_geometryViewsOutput,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    _ => null,
                });
            _geometry = new(_geometryViews, _geometryInstances, _geometryPropertiesInput, new(0,256),
                _geometryVisible, _geometryVisible, _geometryVisible, visibleCapacity, _geometryPageHeap, _geometryPageHeap,
                _initializeGeometry);
            uint alignment = _graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
            _dimensions = edit.CreateBuffer(new(alignment, BufferUsages.Constant), MemoryType.Upload);
            _dimensionsView = edit.CreateBufferCbv(_dimensions);
            _opaqueColor = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, width, height, 1, 1, 1, 1,
                Format.R16G16B16A16Float, TextureUsages.Sampled | TextureUsages.ColorAttachment));
            _opaqueDepth = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, width, height, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.ColorAttachment));
            var clear = (Color: _opaqueColor, Depth: _opaqueDepth, Background: new Vector4(background, 1));
            _clearInputs = edit.AddCopyFramePass("Clear layer behavior inputs", PassQueueSelection.AnyOfType(QueueType.Graphics),
                clear, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphTextureId Color, GraphTextureId Depth, Vector4 Background) data) =>
                {
                    pass.Write(data.Color, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
                    pass.Write(data.Depth, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.RenderTarget,
                        ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Color, GraphTextureId Depth, Vector4 Background) data) =>
                {
                    commands.ClearTexture(commands.GetTexture(data.Color), new(0, 1, 0, 1, TextureAspects.Color), data.Background);
                    commands.ClearTexture(commands.GetTexture(data.Depth), new(0, 1, 0, 1, TextureAspects.Color), Vector4.One);
                });
            edit.Commit();
        }
        finally { edit.Dispose(); }
        Composition = new(_graphics.Pipelines, Graph, Prepare, width, height, 1, store, composite, tileSize, blockSize, blockCapacity);
        FirstPass = Composition.AddPass(new("Layer behavior", sortMode, ClipByOpaqueDepth: false, Accumulator: accumulator));
        Composition.ConfigureVisibility(RegisterVisibility);
        Composition.ConfigureShading(_geometry, RegisterShading);
        edit = Graph.BeginEdit();
        try
        {
            _colorView = edit.CreateTextureSrv(Composition.Color, format: Format.R16G16B16A16Float,
                dimension: TextureViewDimension.Texture2DArray);
            _colors = edit.CreatePersistentBuffer(new((ulong)width * height * 16,
                BufferUsages.ShaderWrite | BufferUsages.CopySource));
            _colorsView = edit.CreateBufferUav(_colors, structureStride: 16);
            _colorCopy = Add(ref edit, _readColor, "Capture composed layer colors", default,
                new DispatchArguments((width * height + 63u) / 64u, 1, 1),
                (name, _) => name switch
                {
                    "LayerTestDimensions" => GraphParameterResourceBinding.ConstantBuffer(_dimensionsView, PipelineSync.ComputeShading),
                    "LayerTestComposedColor" => GraphParameterResourceBinding.SampledTexture(_colorView, PipelineSync.ComputeShading),
                    "LayerTestColors" => GraphParameterResourceBinding.WritableBuffer(_colorsView, PipelineSync.ComputeShading,
                        GraphAccessMode.Write, WriteCoverage.Complete),
                    _ => null,
                });
            edit.OrderAfter(_colorCopy, Composition.CompletePass);
            _colorReadback = edit.AddReadback(_colors, BufferRange.Whole, _result, "Layer behavior image");
            edit.OrderAfter(_colorReadback, _colorCopy);
            edit.Commit();
        }
        finally { edit.Dispose(); }

        Shader Load(string name, string source) => _shaders.CreateAsync(name, source,
            File.ReadAllText(Path.Combine(directory, source))).AsTask().GetAwaiter().GetResult();
    }

    internal void OrderColorCapture()
    {
        RenderGraphEdit edit = Graph.BeginEdit();
        try { edit.OrderAfter(_colorCopy, Composition.CompletePass); edit.Commit(); }
        finally { edit.Dispose(); }
    }

    private LayerStagePasses RegisterVisibility(ref RenderGraphEdit edit, in LayerStageResources resources)
    {
        uint tilesX = (_width + Composition.TileSize - 1u) / Composition.TileSize;
        uint tilesY = (_height + Composition.TileSize - 1u) / Composition.TileSize;
        DispatchArguments dispatch = new(tilesX * tilesY, 1, 1);
        LayerStageResources copied = resources;
        GraphPassId emit = Add(ref edit, _emit, "Emit visible layer records", resources.VisibilityRegion, dispatch,
            (name, type) => name == "LayerTestFragments"
                ? GraphParameterResourceBinding.ReadOnlyBuffer(_sourcesView, PipelineSync.ComputeShading)
                : copied.ResolveVisibility(name, type));
        edit.OrderAfter(emit, _initializeGeometry);
        return new(emit, emit, default, default);
    }

    private LayerStagePasses RegisterShading(ref RenderGraphEdit edit, in LayerStageResources resources)
    {
        uint tilesX = (_width + Composition.TileSize - 1u) / Composition.TileSize;
        uint tilesY = (_height + Composition.TileSize - 1u) / Composition.TileSize;
        DispatchArguments dispatch = new(tilesX * tilesY, 1, 1);
        LayerStageResources copied = resources;
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "LayerTestMaterials" => GraphParameterResourceBinding.ReadOnlyBuffer(_paletteView, PipelineSync.ComputeShading),
                "LayerTestVisibleClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(_geometryVisibleView,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix),
                "LayerTestInstanceData" => GraphParameterResourceBinding.ReadOnlyBuffer(_geometryInstancesView,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                "LayerTestPageHeap" => GraphParameterResourceBinding.ReadOnlyBuffer(_geometryPageView, PipelineSync.ComputeShading),
                "LayerTestInstanceProperties" => GraphParameterResourceBinding.ConstantBuffer(_geometryPropertiesView, PipelineSync.ComputeShading),
                "LayerTestFragmentBlocks" => GraphParameterResourceBinding.ReadOnlyBuffer(copied.Fragments.BlocksSrv,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                "LayerTestTileHeads" => GraphParameterResourceBinding.ReadOnlyBuffer(copied.Fragments.TileHeadsSrv,
                    PipelineSync.ComputeShading),
                _ => copied.ResolveShading(name, type),
            };
        GraphPassId shade = Add(ref edit, _shade, "Write layer color payloads", resources.ShadingRegion, dispatch, Resolve);
        return new(default, default, shade, shade);
    }

    private GraphPassId Add(ref RenderGraphEdit edit, LinkedComputePipeline pipeline, string label,
        GraphRegionId region, DispatchArguments dispatch,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        GraphPassId id = edit.AddComputeFramePass(label, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups), region);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(_producer, pipeline, resolve)) pass.Bind(binding);
        return id;
    }

    internal RenderGraphFrame BeginFrame(in RenderGraphFrameOptions options = default)
    {
        Assert.True(Graph.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(10)));
        using (next)
        {
            Prepare.Update();
            return Graph.BeginFrame(next, in options);
        }
    }

    internal void Record(ref RenderGraphFrame frame, scoped ReadOnlySpan<LayerTestFragment> fragments,
        scoped ReadOnlySpan<LayerTestMaterial> palette, scoped ReadOnlySpan<LayerOperation> operations,
        scoped ReadOnlySpan<LayerMaterialComposition> materials)
    {
        LayerTestFragment[] sources = fragments.ToArray();
        Dictionary<uint,uint> identities = sources.Select(value => value.Identity).Distinct().Order()
            .Select((value,index) => (value,index)).ToDictionary(value => value.value, value => (uint)value.index);
        for (int index = 0; index < sources.Length; index++) sources[index] = sources[index] with { Identity = identities[sources[index].Identity] };
        frame.SetBufferInput(_sources, frame.Upload<LayerTestFragment>(sources, BufferUsages.ShaderRead, "Layer behavior fragments"));
        frame.WriteBuffer(_geometryPageHeap, 0, _pageBytes);
        frame.SetBufferInput(_geometryPropertiesInput, new(_geometryProperties,new(0,256)));
        _geometry.SetFrameRanges(ref frame,[new(0,(ulong)_pageBytes.Length)],[]);
        frame.SetBufferInput(_palette, frame.Upload<LayerTestMaterial>(palette, BufferUsages.ShaderRead, "Layer behavior colors"));
        var dimensions = new Dimensions(_width, _height);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(_dimensions), in dimensions);
        Composition.Record(ref frame, _opaqueColor, _opaqueDepth, operations, materials);
    }

    internal void Wait(ulong identity) => Assert.Equal(WaitStatus.Completed, Graph.WaitForFrame(identity, TimeSpan.FromSeconds(20)));

    public void Dispose()
    {
        if (!Graph.IsLost)
        {
            RenderGraphEdit edit = Graph.BeginEdit();
            try
            {
                edit.Remove(_colorReadback);
                edit.Remove(_colorCopy);
                edit.Remove(_colorView);
                edit.Remove(_colorsView);
                edit.Remove(_colors);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            Composition.Dispose();
            edit = Graph.BeginEdit();
            try
            {
                edit.Remove(_initializeGeometry);
                edit.Remove(_clearInputs);
                edit.Remove(_dimensions);
                edit.Remove(_opaqueColor);
                edit.Remove(_opaqueDepth);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        Prepare.Dispose();
        _extraction.Dispose();
        _world.Dispose();
        Graph.Dispose();
        _shaders.Dispose();
        _graphics.Dispose();
    }

    private static string ShaderDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "assets", "Shaders");
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct Dimensions(uint Width, uint Height);
    private sealed class ColorReceiver : IGraphReadbackReceiver
    {
        internal Vector4[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, Vector4>(bytes).ToArray();
    }
    private const string ProducerSource = """
        #include "layer_fragment_store.slang"
        import fragment_material;
        import cluster_view;
        #include "generated/render_instance_layout.slang"
        struct LayerTestFragment
        {
            uint Tile, Pixel; float Depth; uint Identity;
            uint Material, DrawOrder, Pass, Reserved;
        };
        struct LayerTestMaterial { float4 PremultipliedColor; float4 Transmittance; };
        StructuredBuffer<LayerTestFragment> LayerTestFragments;
        StructuredBuffer<LayerTestMaterial> LayerTestMaterials;
        StructuredBuffer<VisibleCluster> LayerTestVisibleClusters;
        ByteAddressBuffer LayerTestInstanceData;
        ByteAddressBuffer LayerTestPageHeap;
        ConstantBuffer<RenderInstancePropertyMetadata> LayerTestInstanceProperties;
        ByteAddressBuffer LayerTestFragmentBlocks;
        StructuredBuffer<uint> LayerTestTileHeads;
        RWStructuredBuffer<VisibleCluster> LayerTestVisibleOutput;
        RWByteAddressBuffer LayerTestInstanceOutput;
        RWByteAddressBuffer LayerTestPropertiesOutput;
        RWStructuredBuffer<ClusterViewUniforms> LayerTestViewsOutput;
        struct Dimensions { uint Width, Height; uint2 Padding; };
        ConstantBuffer<Dimensions> LayerTestDimensions;
        Texture2DArray<float4> LayerTestComposedColor;
        RWStructuredBuffer<float4> LayerTestColors;
        [shader("compute")] [numthreads(64,1,1)]
        void InitializeLayerGeometry(uint lane : SV_GroupIndex)
        {
            uint bytes;
            LayerTestPropertiesOutput.GetDimensions(bytes);
            for (uint word = lane; word < bytes / 4u; word += 64u) LayerTestPropertiesOutput.Store(word * 4u,0u);
            DeviceMemoryBarrierWithGroupSync();
            if (lane == 0u)
            {
                LayerTestPropertiesOutput.Store(CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD * 4u,RENDER_INSTANCE_LINEAR_PER_INSTANCE);
                ClusterViewUniforms view = {};
                LayerTestViewsOutput[0] = view;
            }
            uint count,stride;
            LayerTestFragments.GetDimensions(count,stride);
            for (uint index = lane; index < count; index += 64u)
            {
                LayerTestFragment source = LayerTestFragments[index];
                LayerTestVisibleOutput[source.Identity] = MakeVisibleCluster(MakeDrawRequest(0u,0u,source.Identity,0u),0u.xx,0u,0u);
                LayerTestInstanceOutput.Store(source.Identity * 4u,source.Material);
            }
        }
        [shader("compute")] [numthreads(64,1,1)]
        void EmitLayerRecords(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
        {
            LayerSoftwareFragmentStore.Begin(lane);
            uint waveIndex = WaveReadLaneFirst(lane);
            uint count, stride;
            LayerTestFragments.GetDimensions(count, stride);
            for (uint index = lane; index < count; index += 64u)
            {
                LayerTestFragment source = LayerTestFragments[index];
                if (source.Tile != group.x) continue;
                LayerFragment fragment;
                fragment.Depth = source.Depth;
                fragment.Identity = EncodeVisBuffer(source.Identity,0u);
                fragment.Packed = PackLayerFragment(source.Pixel, source.Pass, source.DrawOrder, source.Reserved);
                LayerSoftwareFragmentStore.Append(group.x, fragment, waveIndex);
            }
            LayerSoftwareFragmentStore.End(lane);
        }
        [shader("compute")] [numthreads(64,1,1)]
        void ShadeLayerRecords(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
        {
            uint block = LayerTestTileHeads[group.x];
            while (block != LAYER_BLOCK_EMPTY)
            {
                uint3 blockWords = LayerTestFragmentBlocks.Load3(GetLayerBlockByteOffset(block,LayerFragmentStore.BlockSize));
                LayerBlockHeader header = { blockWords.x,blockWords.y,blockWords.z };
                for (uint record = lane; record < header.Count; record += 64u)
                {
                    uint index = GetLayerFragmentIndex(block, record, LayerFragmentStore.BlockSize);
                    uint3 words = LayerTestFragmentBlocks.Load3(GetLayerFragmentByteOffset(block,record,LayerFragmentStore.BlockSize));
                    LayerFragment fragment = { asfloat(words.x),words.y,words.z };
                    uint address = ReadFragmentMaterialAddress(LayerTestPageHeap,LayerTestVisibleClusters,
                        LayerTestInstanceData,LoadRenderInstanceMetadata(LayerTestInstanceProperties,
                            CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD),fragment.Identity);
                    LayerTestMaterial material = LayerTestMaterials[address];
                    StoreLayerColor(index, material.PremultipliedColor.rgb, material.Transmittance.rgb);
                }
                block = header.Next;
            }
        }
        [shader("compute")] [numthreads(64,1,1)]
        void ReadLayerColors(uint3 id : SV_DispatchThreadID)
        {
            if (id.x < LayerTestDimensions.Width * LayerTestDimensions.Height)
                LayerTestColors[id.x] = LayerTestComposedColor.Load(int4(id.x % LayerTestDimensions.Width,
                    id.x / LayerTestDimensions.Width, 0, 0));
        }
        """;
}
