using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class SurfacePixelInputTests
{
    [Fact]
    public async Task OffsetDepthMaterialsReceiveTheStoredPositionAndCameraPixelFootprint()
    {
        const uint width = 16, height = 8, x = 5, y = 3;
        const float storedDepth = .72f, domainDepth = .25f;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 2, .5f, 25);
        Matrix4x4.Invert(projection, out Matrix4x4 clipToWorld);
        var parameters = new InputParameters(clipToWorld, x, y, 2, storedDepth, domainDepth);
        using var graphics = new BinningTestDevice();
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "surface_inputs.slang")))
            directory = directory.Parent!;
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [Path.Combine(directory.FullName, "assets", "Shaders")];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader shader = await shaders.CreateAsync("Observed surface inputs", "surface_pixel_input_checks.slang", ShaderSource);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var result = new InputReadback();
        GraphBufferId uniforms;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            uint alignment = graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
            uniforms = edit.CreatePersistentBuffer(new((96u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant, "Observed surface input parameters"), MemoryType.Upload);
            GraphBufferCbvId parametersView = edit.CreateBufferCbv(uniforms);
            GraphBufferId values = edit.CreatePersistentBuffer(new(64, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            GraphBufferUavId valuesView = edit.CreateBufferUav(values, structureStride: 16);
            var bindings = new Dictionary<string, GraphParameterResourceBinding>
            {
                ["InputObservation"] = GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
                ["ObservedSurfaceInputs"] = GraphParameterResourceBinding.WritableBuffer(valuesView,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            };
            GraphPassId id = Add(ref edit, graphics.Pipelines, shader, "ObserveOffsetSurfaceInput", new(1, 1, 1), bindings);
            GraphReadbackId readback = edit.AddReadback(values, BufferRange.Whole, result, "Read material surface inputs");
            edit.OrderAfter(readback, id);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        RenderGraphFrame frame = graph.BeginFrame();
        ulong identity;
        try
        {
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(uniforms), in parameters);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal(storedDepth, result.Values[0].X);
        Assert.Equal(2f, result.Values[0].Y);
        Assert.Equal(2f, result.Values[0].W);
        Assert.InRange(MathF.Abs(Project(x, y, domainDepth).Z - result.Values[0].Z), 0, .00001f);
        Vector3 expected = Project(x, y, storedDepth);
        Assert.InRange(Vector3.Distance(expected, Position(result.Values[1])), 0, .00001f);
        Assert.InRange(Vector3.Distance(Project(x + 1, y, storedDepth) - expected, Position(result.Values[2])), 0, .00001f);
        Assert.InRange(Vector3.Distance(Project(x, y + 1, storedDepth) - expected, Position(result.Values[3])), 0, .00001f);

        Vector3 Project(uint pixelX, uint pixelY, float depth)
        {
            Vector4 clip = new((pixelX + .5f) / width * 2 - 1, 1 - (pixelY + .5f) / height * 2, depth, 1);
            Vector4 position = Vector4.Transform(clip, clipToWorld);
            return new Vector3(position.X, position.Y, position.Z) / position.W;
        }
    }

    [Fact]
    public async Task NonAtomicVisibilityResolvesTheOriginalIdentityAndFullDepth()
    {
        const uint identityValue = 1234567;
        const float depthValue = .12345679f;
        using var graphics = new BinningTestDevice();
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "visibility_resolve_buffer.slang")))
            directory = directory.Parent!;
        string shaderDirectory = Path.Combine(directory.FullName, "assets", "Shaders");
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [shaderDirectory];
        settings.Targets =
        [
            new() { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_0") },
            new() { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") },
        ];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader clearShader = await shaders.CreateAsync("NonAtomic visibility clear", "visibility_clear_buffer.slang",
            File.ReadAllText(Path.Combine(shaderDirectory, "visibility_clear_buffer.slang")));
        Shader resolveShader = await shaders.CreateAsync("NonAtomic visibility resolve", "visibility_resolve_buffer.slang",
            File.ReadAllText(Path.Combine(shaderDirectory, "visibility_resolve_buffer.slang")));
        Shader writer = await shaders.CreateAsync("NonAtomic visibility output", "non_atomic_visibility_check.slang",
            NonAtomicSource);
        foreach (SlangCompileTarget target in new[] { SlangCompileTarget.Dxil, SlangCompileTarget.Spirv })
            foreach (var item in new[]
                     {
                         (clearShader, "ClearVisibilityNonAtomic", SlangStage.Compute),
                         (resolveShader, "PSResolveVisibilityNonAtomic", SlangStage.Fragment),
                         (writer, "WriteNonAtomicVisibility", SlangStage.Compute),
                     })
            {
                int targetIndex = item.Item1.RequireSlangTarget(target);
                EntryPointReflection entry = item.Item1.RequireSlangEntry(targetIndex, item.Item2, item.Item3);
                Assert.True(item.Item1.GetSlangEntryBlob(targetIndex, entry)?.GetBufferSize() > 0);
            }
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var result = new InputReadback();
        GraphBufferId uniforms;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            uniforms = edit.CreatePersistentBuffer(new(graphics.Device.Capabilities.Limits.ConstantBufferAlignment,
                BufferUsages.Constant), MemoryType.Upload);
            GraphBufferCbvId uniformView = edit.CreateBufferCbv(uniforms);
            GraphBufferId storage = edit.CreatePersistentBuffer(new(16, BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
            GraphBufferUavId storageWrite = edit.CreateBufferUav(storage);
            GraphBufferSrvId storageRead = edit.CreateBufferSrv(storage);
            GraphTextureId visibility = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1,
                Format.R32UInt, TextureUsages.ColorAttachment | TextureUsages.Sampled));
            GraphTextureId depth = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1,
                Format.D32Float, TextureUsages.DepthStencilAttachment | TextureUsages.Sampled));
            GraphColorAttachmentViewId colorView = edit.CreateColorAttachmentView(visibility,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2DArray);
            GraphDepthStencilViewId depthView = edit.CreateDepthStencilView(depth,
                new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray);
            var computeBindings = new Dictionary<string, GraphParameterResourceBinding>
            {
                ["Uniforms"] = GraphParameterResourceBinding.ConstantBuffer(uniformView, PipelineSync.ComputeShading),
                ["Winner"] = GraphParameterResourceBinding.WritableBuffer(storageWrite, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete),
                ["VisibilityNonAtomicBuffer"] = GraphParameterResourceBinding.WritableBuffer(storageWrite,
                    PipelineSync.ComputeShading, GraphAccessMode.ReadWrite, WriteCoverage.Partial),
            };
            GraphPassId clear = Add(ref edit, graphics.Pipelines, clearShader, "ClearVisibilityNonAtomic", new(1, 1, 1), computeBindings);
            GraphPassId write = Add(ref edit, graphics.Pipelines, writer, "WriteNonAtomicVisibility", new(1, 1, 1), computeBindings);
            edit.OrderAfter(write, clear);
            LinkedRasterPipeline pipeline = LinkedRasterPipeline.Create(graphics.Pipelines, resolveShader,
                "VSFullscreen", "PSResolveVisibilityNonAtomic", [Format.R32UInt], Format.D32Float,
                rasterizer: new(Cull: CullType.None),
                depthStencil: new(DepthTest: true, DepthWrite: true, DepthComparison: CompareOperation.Always));
            GraphPassId resolve = edit.AddRasterFramePass("Resolve NonAtomic visibility",
                PassQueueSelection.AnyOfType(QueueType.Graphics), new DrawArguments(3, 1, 0, 0),
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition pass, ref DrawArguments _) =>
                {
                    pass.DefineRasterExtent(2, 1);
                    pass.ColorAttachment(0, colorView, LoadType.Discard, StoreType.Store, WriteCoverage.Complete, default);
                    pass.DepthStencilAttachment(depthView, LoadType.Discard, StoreType.Store, WriteCoverage.Complete, 1f,
                        LoadType.Discard, StoreType.Discard, WriteCoverage.Complete, 0);
                },
                static (ref RasterPassCommandScope commands, in DrawArguments draw) =>
                {
                    commands.SetViewports([new(0, 0, 2, 1)]);
                    commands.SetScissors([new(0, 0, 2, 1)]);
                    commands.Draw(draw);
                });
            PassDefinition resolvePass = edit.GetPass(resolve);
            resolvePass.SetPipeline(pipeline.Pipeline);
            foreach (ShaderParameterResource resource in ShaderParameterResources.Create(resolveShader,
                         graphics.Device.Capabilities.CompileTarget, "VSFullscreen", "PSResolveVisibilityNonAtomic").Resources)
                if (resource.Type == ResourceBindingType.None) resolvePass.SetParameterBlock(resource.ParameterLayout);
                else if (!resource.IsUsed) resolvePass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                else if (resource.Name == "Uniforms")
                    resolvePass.Bind(GraphParameterResourceBinding.ConstantBuffer(uniformView, PipelineSync.PixelShading));
                else resolvePass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(storageRead, PipelineSync.PixelShading));
            edit.OrderAfter(resolve, write);
            GraphBufferId values = edit.CreatePersistentBuffer(new(32, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            var observationBindings = new Dictionary<string, GraphParameterResourceBinding>
            {
                ["NonAtomicVisibility"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(visibility,
                    format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading),
                ["NonAtomicDepth"] = GraphParameterResourceBinding.SampledTexture(edit.CreateTextureSrv(depth,
                    new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading),
                ["NonAtomicObservedValues"] = GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(values,
                    structureStride: 16), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            };
            GraphPassId observe = Add(ref edit, graphics.Pipelines, writer, "ObserveNonAtomicVisibility", new(1, 1, 1), observationBindings);
            edit.OrderAfter(observe, resolve);
            GraphReadbackId readback = edit.AddReadback(values, BufferRange.Whole, result, "Read NonAtomic visibility and depth");
            edit.OrderAfter(readback, observe);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        RenderGraphFrame frame = graph.BeginFrame();
        ulong identity;
        try
        {
            new uint[] { 2, 1, 1, 0 }.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(frame.GetBufferDataWriteSpan(uniforms)));
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        Assert.Equal((float)identityValue, result.Values[0].X);
        Assert.Equal(depthValue, result.Values[0].Y);
        Assert.Equal(0f, result.Values[1].X);
        Assert.Equal(1f, result.Values[1].Y);
    }

    private const string NonAtomicSource = """
        import cluster_visibility_raster_output;
        Texture2DArray<uint> NonAtomicVisibility;
        Texture2DArray<float> NonAtomicDepth;
        RWStructuredBuffer<float4> NonAtomicObservedValues;
        [shader("compute")][numthreads(1,1,1)]
        void WriteNonAtomicVisibility()
        {
            WriteVisibilityNonAtomic(int2(0,0),0u,2u,1u,.12345679,1234567u);
        }
        [shader("compute")][numthreads(2,1,1)]
        void ObserveNonAtomicVisibility(uint3 thread:SV_DispatchThreadID)
        {
            uint3 pixel = uint3(thread.x,0u,0u);
            NonAtomicObservedValues[thread.x] = float4(float(NonAtomicVisibility[pixel]),NonAtomicDepth[pixel],0,0);
        }
        """;

    private static Vector3 Position(Vector4 value) => new(value.X, value.Y, value.Z);

    private static GraphPassId Add(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, DispatchArguments dispatch, Dictionary<string, GraphParameterResourceBinding> bindings)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline,
            (name, _) => bindings.TryGetValue(name, out var binding) ? binding : null)) pass.Bind(binding);
        return id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
    private readonly record struct InputParameters(Matrix4x4 ClipToWorld, uint X, uint Y, uint View,
        float StoredDepth, float DomainDepth);

    private sealed class InputReadback : IGraphReadbackReceiver
    {
        internal Vector4[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, Vector4>(bytes).ToArray();
    }

    private const string ShaderSource = """
        __exported import surface_inputs;
        __exported import shade_surface_entries;
        __exported import triangle_surface;
        struct InputObservationParameters
        {
            float4x4 ClipToWorld;
            uint3 Pixel;
            float StoredDepth;
            float DomainDepth;
            uint3 Padding;
        };
        ConstantBuffer<InputObservationParameters> InputObservation;
        RWStructuredBuffer<float4> ObservedSurfaceInputs;
        struct InputObservationOutput : ISurfaceOutput
        {
            uint Index;
            [mutating] void SetIndex(uint index) { Index = index; }
            void Write(uint3 pixel, float4 color) { ObservedSurfaceInputs[Index] = color; }
            void Write(uint3 pixel, float3 premultipliedColor, float3 transmittance)
            { ObservedSurfaceInputs[Index] = float4(premultipliedColor, transmittance.x); }
        };
        struct InputObservationMaterial : IMaterial
        {
            typedef StaticVertexEval Vertex;
            typedef LinearSurface Domain;
            typedef OpaqueVisibilityEvaluate<Vertex.Vertex> Visibility;
            void evaluateSurface<TOutput : ISurfaceOutput>(SurfacePixelInput input, SurfaceEval surface,
                float3 bary, Triangle triangle, TOutput output)
            {
                output.SetIndex(0u);
                output.Write(input.PixelCoord, float4(input.Depth, float(input.VisibilityDepth), surface.Position.z, float(input.PixelCoord.z)));
                output.SetIndex(1u);
                output.Write(input.PixelCoord, float4(input.PositionFromDepth, 1));
                output.SetIndex(2u);
                output.Write(input.PixelCoord, float4(input.PositionDx, 1));
                output.SetIndex(3u);
                output.Write(input.PixelCoord, float4(input.PositionDy, 1));
            }
            void evaluateDebug<TOutput : ISurfaceOutput>(uint mode, SurfacePixelInput input,
                SurfaceEval surface, float3 bary, Triangle triangle, TOutput output)
            { evaluateSurface(input, surface, bary, triangle, output); }
        };
        [shader("compute")][numthreads(1,1,1)]
        void ObserveOffsetSurfaceInput()
        {
            TriangleInfo triangleInfo = {};
            ClusterViewUniforms view = {};
            view.ScreenWidth = 16u;
            view.ScreenHeight = 8u;
            view.ClipToWorld = InputObservation.ClipToWorld;
            SurfaceBarycentrics bary = {};
            bary.Value = float3(.2,.3,.5);
            bary.Dx = float3(-.01,.01,0);
            bary.Dy = float3(-.01,0,.01);
            float3 storedPosition = ReconstructWorldPositionAt(float2(InputObservation.Pixel.xy) + .5,
                InputObservation.StoredDepth, view);
            SurfacePixelInput input = BuildSurfacePixelInput(triangleInfo, view, InputObservation.Pixel, bary,
                InputObservation.StoredDepth, 2u, storedPosition);
            SurfaceEval surface = {};
            surface.Position = ReconstructWorldPositionAt(float2(InputObservation.Pixel.xy) + .5,
                InputObservation.DomainDepth, view);
            InputObservationMaterial material;
            InputObservationOutput output;
            Triangle triangle = {};
            material.evaluateSurface(input, surface, bary.Value, triangle, output);
        }
        [shader("compute")][numthreads(64,1,1)]
        void ObserveOpaqueSurfaceInput(uniform StaticVertexEval vertex, uniform LinearSurface domain,
            uniform InputObservationMaterial material, uint3 thread : SV_DispatchThreadID)
        {
            VisibilityBufferInput<StaticVertexEval,LinearSurface> source;
            InputObservationOutput output;
            ShadeSurface<InputObservationMaterial,VisibilityBufferInput<StaticVertexEval,LinearSurface>,InputObservationOutput,0u>(
                material,vertex,domain,source,output,thread);
        }
        [shader("compute")][numthreads(64,1,1)]
        void ObserveLayerSurfaceInput(uniform StaticVertexEval vertex, uniform LinearSurface domain,
            uniform InputObservationMaterial material, uint3 thread : SV_DispatchThreadID)
        {
            LayerFragmentInput<StaticVertexEval,LinearSurface> source;
            InputObservationOutput output;
            ShadeSurface<InputObservationMaterial,LayerFragmentInput<StaticVertexEval,LinearSurface>,InputObservationOutput,0u>(
                material,vertex,domain,source,output,thread);
        }
        """;
}
