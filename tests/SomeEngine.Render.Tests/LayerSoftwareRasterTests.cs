using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class LayerSoftwareRasterTests
{
    [Fact]
    public async Task ImportedLayerRasterPreservesVerticesAndVaryingsAcrossDeviceWaves()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "SomeEngine-LayerRaster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string shaders = ShaderDirectory();
            string path = Path.Combine(temporary, "wave_material.shader");
            await File.WriteAllTextAsync(Path.Combine(temporary, "wave_material.slang"), MaterialSource);
            using var definition = new ShaderAsset { Name = "Layer vertex transport" };
            definition.GetBlock<ShaderSourceBlock>().SourceNames = ["wave_material.slang"];
            using ShaderAsset built = SlangShaderImporter.CompileDefinition(definition, path,
                [SlangCompileTarget.Dxil], includeDirectories: [shaders], projectRoot: temporary);
            IGlobalSession slang = SlangShaderImporter.GlobalSession;
            SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path,
                SlangShaderImporter.CreateTargets(slang, [SlangCompileTarget.Dxil]), [shaders]);
            using var library = new ShaderLibrary(slang, settings);
            Shader shader = await library.CreateAsync(built);
            Shader raster = shader.RequireSpecialization("LayerRasterSoftware", ["LayerNoPageRequests"]);
            Shader initialize = await library.CreateAsync("Layer raster inputs", "layer_raster_inputs.slang", InitializationSource);
            using var graphics = new BinningTestDevice();
            using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
                [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
            using MeshAsset meshAsset = CreateMesh();
            Assert.Single(meshAsset.GetBlock<ClusterGeometryBlock>().Clusters);
            Assert.Equal(36, meshAsset.GetBlock<ClusterGeometryBlock>().Clusters[0].Vertices.Length);
            Mesh mesh = await meshAsset.GetBlock<ClusterMeshBlock>().CreateAsync(meshAsset.GetBlock<MeshBlock>(), "Layer triangles");
            byte[] page = new byte[mesh.GetPageByteLength(0)];
            mesh.ReadPage(0, page);
            var observed = new RasterReadback<Vector4>();
            GraphBufferInput pageInput, visibleInput, materialsInput, stagesInput;
            GraphBufferId storeUniforms, compositionUniforms, stageUniforms;
            RenderGraphEdit edit = graph.BeginEdit();
            try
            {
                pageInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
                visibleInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
                materialsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
                stagesInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
                uint alignment = graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
                storeUniforms = edit.CreateBuffer(new(alignment, BufferUsages.Constant), MemoryType.Upload);
                compositionUniforms = edit.CreateBuffer(new(alignment, BufferUsages.Constant), MemoryType.Upload);
                stageUniforms = edit.CreateBuffer(new(alignment, BufferUsages.Constant), MemoryType.Upload);
                ShaderReflection reflection = initialize.GetSlangReflection(0);
                uint viewStride = (uint)reflection.GetTypeLayout(reflection.FindTypeByName("ClusterViewUniforms")!.Value,
                    LayoutRules.DefaultStructuredBuffer)!.Value.GetSize(SlangParameterCategory.Uniform);
                GraphBufferId views = Buffer(ref edit, viewStride);
                GraphBufferId instanceData = Buffer(ref edit, 64);
                GraphBufferId properties = edit.CreatePersistentBuffer(new(alignment,
                    BufferUsages.Constant | BufferUsages.ShaderWrite));
                GraphBufferId workRanges = Buffer(ref edit, 8);
                GraphBufferId works = Buffer(ref edit, 16);
                GraphBufferId clusters = Buffer(ref edit, 4);
                GraphBufferId blocks = Buffer(ref edit, 16 * (12 + 32 * 12));
                GraphBufferId heads = Buffer(ref edit, 4);
                GraphBufferId counters = Buffer(ref edit, 32);
                GraphBufferId varyings = Buffer(ref edit, 16 * 16 * 16);
                GraphTextureId depth = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 16, 16, 1, 1, 1, 1,
                    Format.R32Float, TextureUsages.Sampled | TextureUsages.ColorAttachment));
                GraphPassId clearDepth = edit.AddCopyFramePass("Clear layer raster depth", PassQueueSelection.AnyOfType(QueueType.Graphics), depth,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref GraphTextureId texture) => pass.Write(texture, new(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in GraphTextureId texture) => commands.ClearTexture(commands.GetTexture(texture),
                        new(0, 1, 0, 1, TextureAspects.Color), Vector4.One));
                var bindings = new Dictionary<string, GraphParameterResourceBinding>
                {
                    ["PageHeap"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(pageInput), PipelineSync.ComputeShading),
                    ["LayerVisibleClusters"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(visibleInput, structureStride: 16), PipelineSync.ComputeShading),
                    ["LayerRasterMaterials"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(materialsInput, structureStride: 8), PipelineSync.ComputeShading),
                    ["LayerMaterialRasterStages"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(stagesInput, structureStride: 4), PipelineSync.ComputeShading),
                    ["LayerRasterViews"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(views, structureStride: viewStride), PipelineSync.ComputeShading),
                    ["InstanceData"] = GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(instanceData), PipelineSync.ComputeShading),
                    ["InstanceProperties"] = GraphParameterResourceBinding.ConstantBuffer(edit.CreateBufferCbv(properties), PipelineSync.ComputeShading),
                    ["LayerFragmentStore"] = GraphParameterResourceBinding.ConstantBuffer(edit.CreateBufferCbv(storeUniforms), PipelineSync.ComputeShading),
                    ["LayerCompositionUniforms"] = GraphParameterResourceBinding.ConstantBuffer(edit.CreateBufferCbv(compositionUniforms), PipelineSync.ComputeShading),
                    ["LayerRasterStage"] = GraphParameterResourceBinding.ConstantBuffer(edit.CreateBufferCbv(stageUniforms), PipelineSync.ComputeShading),
                    ["LayerOpaqueDepth"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(depth, dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading),
                    ["RasterTestViews"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(views, structureStride: viewStride)),
                    ["RasterTestInstanceData"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(instanceData)),
                    ["RasterTestInstanceProperties"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(properties)),
                    ["LayerStageWorkRanges"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(workRanges, structureStride: 8)),
                    ["LayerStageWorks"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(works, structureStride: 16)),
                    ["LayerStageClusters"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(clusters, structureStride: 4)),
                    ["LayerFragmentBlocks"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(blocks)),
                    ["LayerTileHeads"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(heads, structureStride: 4)),
                    ["LayerFrameCounters"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(counters, structureStride: 4)),
                    ["RasterTestVaryings"] = GraphParameterResourceBinding.WritableBufferDescriptor(edit.CreateBufferUav(varyings, structureStride: 16)),
                };
                GraphPassId init = Add(ref edit, initialize, "InitializeLayerRaster", graphics.Pipelines, bindings,
                    [new(views, GraphAccessMode.Write), new(instanceData, GraphAccessMode.Write), new(properties, GraphAccessMode.Write),
                        new(workRanges, GraphAccessMode.Write), new(works, GraphAccessMode.Write), new(clusters, GraphAccessMode.Write),
                        new(heads, GraphAccessMode.Write), new(counters, GraphAccessMode.Write), new(varyings, GraphAccessMode.Write)]);
                GraphPassId render = Add(ref edit, raster, "LayerRasterSoftware", graphics.Pipelines, bindings,
                    [new(workRanges, GraphAccessMode.Read), new(works, GraphAccessMode.Read), new(clusters, GraphAccessMode.Read),
                        new(blocks, GraphAccessMode.Write, BufferContentScope.CountBoundedRanges), new(heads, GraphAccessMode.ReadWrite),
                        new(counters, GraphAccessMode.ReadWrite), new(varyings, GraphAccessMode.ReadWrite)]);
                edit.OrderAfter(render, init);
                edit.OrderAfter(render, clearDepth);
                GraphReadbackId observedRead = edit.AddReadback(varyings, BufferRange.Whole, observed, "Layer vertex varying results");
                edit.OrderAfter(observedRead, render);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            RenderGraphFrame frame = graph.BeginFrame();
            ulong identity;
            try
            {
                frame.SetBufferInput(pageInput, frame.Upload<byte>(page, BufferUsages.ShaderRead));
                frame.SetBufferInput(visibleInput, frame.Upload<uint>(new uint[4], BufferUsages.ShaderRead));
                frame.SetBufferInput(materialsInput, frame.Upload<uint>(new uint[] { 0, 0 }, BufferUsages.ShaderRead));
                frame.SetBufferInput(stagesInput, frame.Upload<uint>(new uint[] { 0 }, BufferUsages.ShaderRead));
                Write(ref frame, storeUniforms, [1, 32, 16, 0]);
                Write(ref frame, compositionUniforms, [16, 16, 1, 16, 1, 1, 1, 32, 16, 512, 1, 0, 0, 0, 0, 0]);
                Write(ref frame, stageUniforms, [0, 0, 0, 0]);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(20)));
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 4; column++)
                {
                    int x = column * 4 + 1, y = row * 4 + 1;
                    float worldX = (x + .5f) / 8 - 1, worldY = 1 - (y + .5f) / 8;
                    Vector4 expected = new(worldX, worldY, .4f, worldX * 2 + worldY * 3);
                    Assert.InRange(Vector4.Distance(expected, observed.Values[y * 16 + x]), 0, .0001f);
                }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static MeshAsset CreateMesh()
    {
        var positions = new List<Vector3>();
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 4; column++)
            {
                float x = column * 4, y = row * 4;
                positions.Add(new(x / 8 - 1, 1 - y / 8, .4f));
                positions.Add(new((x + 3.5f) / 8 - 1, 1 - y / 8, .4f));
                positions.Add(new(x / 8 - 1, 1 - (y + 3.5f) / 8, .4f));
            }
        return ClusterBuilder.ProcessRaw(positions.ToArray(), [],
            Enumerable.Range(0, positions.Count).Select(index => (uint)index).ToArray(), [new(Guid.Empty)], "Layer wave triangles");
    }

    private static GraphBufferId Buffer(ref RenderGraphEdit edit, ulong bytes) =>
        edit.CreatePersistentBuffer(new(bytes, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource));

    private static void Write(ref RenderGraphFrame frame, GraphBufferId buffer, uint[] words) =>
        MemoryMarshal.AsBytes(words.AsSpan()).CopyTo(frame.GetBufferDataWriteSpan(buffer));

    private static GraphPassId Add(ref RenderGraphEdit edit, Shader shader, string entry, RenderPipelines pipelines,
        Dictionary<string, GraphParameterResourceBinding> bindings, Access[] accesses)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), new DispatchArguments(1, 1, 1),
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline, (name, _) => bindings[name])) pass.Bind(binding);
        foreach (Access access in accesses)
            if (access.Mode == GraphAccessMode.Read)
                pass.Read(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            else if (access.Mode == GraphAccessMode.ReadWrite)
                pass.ReadWrite(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            else
                pass.Write(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
                    access.Scope == BufferContentScope.FullRange ? WriteCoverage.Complete : WriteCoverage.Partial,
                    contentScope: access.Scope);
        return id;
    }

    private static string ShaderDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "layer_tile_raster.slang"))) directory = directory.Parent!;
        return Path.Combine(directory.FullName, "assets", "Shaders");
    }

    private readonly record struct Access(GraphBufferId Buffer, GraphAccessMode Mode,
        BufferContentScope Scope = BufferContentScope.FullRange);

    private sealed class RasterReadback<T> : IGraphReadbackReceiver where T : unmanaged
    {
        internal T[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    private const string MaterialSource = """
        import material_interfaces;
        import vertex_evaluate;
        import triangle_surface;
        RWStructuredBuffer<float4> RasterTestVaryings;
        struct RasterTestVarying { float3 Position:TEXCOORD0; float Marker:TEXCOORD1; };
        [VisibilityDepth("opaque")]
        [MaterialDepthInputs("")]
        struct RasterTestVisibility : IVisibilityEvaluate<StandardVertex>
        {
            typedef RasterTestVarying Varying;
            Varying makeVarying(StandardVertex vertex,VisibilityVertexInput input)
            { Varying result={vertex.position,vertex.position.x*2.0+vertex.position.y*3.0}; return result; }
            Varying readLane(Varying value,uint lane)
            { Varying result={WaveReadLaneAt(value.Position,lane),WaveReadLaneAt(value.Marker,lane)};return result; }
            Varying interpolateVertex(Varying a,Varying b,Varying c,float3 bary)
            { Varying result={a.Position*bary.x+b.Position*bary.y+c.Position*bary.z,a.Marker*bary.x+b.Marker*bary.y+c.Marker*bary.z};return result; }
            Varying interpolatePixel(Varying a,Varying b,Varying c,VisibilityInterpolationInput input)
            {return interpolateVertex(a,b,c,input.PerspectiveBarycentric);}
            VisibilityPixelResult evaluate(VisibilityPixelInput input,Varying varying)
            {
                RasterTestVaryings[input.PixelCoord.y*16u+input.PixelCoord.x]=float4(varying.Position,varying.Marker);
                VisibilityPixelResult result={1u,input.Depth};return result;
            }
        };
        [VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
        [ConservativeBounds("finite")][RasterFaces("both","both")]
        struct WaveMaterial : IMaterial
        {
            typedef StaticVertexEval Vertex;
            typedef LinearSurface Domain;
            typedef RasterTestVisibility Visibility;
            void evaluateSurface<TOutput:ISurfaceOutput>(SurfacePixelInput input,Domain.Surface surface,
                float3 bary,Domain.TriangleData triangle,TOutput output){output.Write(input.PixelCoord,float4(1));}
            void evaluateDebug<TOutput:ISurfaceOutput>(uint mode,SurfacePixelInput input,Domain.Surface surface,
                float3 bary,Domain.TriangleData triangle,TOutput output){evaluateSurface(input,surface,bary,triangle,output);}
        };
        """;

    private const string InitializationSource = """
        __exported import layer_tile_raster;
        RWStructuredBuffer<ClusterViewUniforms> RasterTestViews;
        RWByteAddressBuffer RasterTestInstanceData;
        RWByteAddressBuffer RasterTestInstanceProperties;
        RWStructuredBuffer<float4> RasterTestVaryings;
        [shader("compute")][numthreads(32,1,1)]
        void InitializeLayerRaster(uint lane:SV_GroupIndex)
        {
            for(uint pixel=lane;pixel<256u;pixel+=32u)RasterTestVaryings[pixel]=float4(-99.0);
            for(uint word=lane;word<64u;word+=32u)RasterTestInstanceProperties.Store(word*4u,0u);
            if(lane==0u)
            {
                ClusterViewUniforms view={};
                view.ViewProj=view.View=view.ClipToWorld=float4x4(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1);
                view.ScreenWidth=view.ScreenHeight=16u;
                RasterTestViews[0]=view;
                for(uint word=0u;word<16u;word++)RasterTestInstanceData.Store(word*4u,0u);
                RenderInstanceTransform transform={};transform.Rotation.w=1.0;transform.Scale=1.0;transform.Stretch=float3(1.0);
                RasterTestInstanceData.Store<RenderInstanceTransform>(0u,transform);
                LayerStageWork work={0u,0u,0u,1u};LayerStageWorks[0]=work;
                LayerStageWorkRanges[0]=uint2(0u,1u);LayerStageClusters[0]=0u;
            }
            AllMemoryBarrierWithGroupSync();
            if(lane==0u)
            {
                RasterTestInstanceProperties.Store(CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD*4u,48u);
                RasterTestInstanceProperties.Store(RENDER_FIRST_PERSON_METADATA_WORD*4u,52u);
            }
            if(lane==0u)LayerTileHeads[0]=LAYER_BLOCK_EMPTY;
            if(lane<LAYER_FRAME_COUNTER_COUNT)
                LayerFrameCounters[lane]=lane==LAYER_FRAME_SOURCE_BLOCK_CAPACITY?16u:0u;
        }
        """;
}
