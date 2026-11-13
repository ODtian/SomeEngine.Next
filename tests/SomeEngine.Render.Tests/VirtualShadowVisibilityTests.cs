using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowVisibilityTests
{
    [Theory]
    [InlineData(.25f, -.35f)]
    [InlineData(-.4f, .2f)]
    public void OpaqueDepthReconstructsThePlanePositionAndGeometricNormal(float normalX, float normalY)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100);
        Matrix4x4.Invert(projection, out Matrix4x4 clipToWorld);
        Vector3 normal = new(normalX, normalY, 1);
        var parameters = new Parameters(projection, clipToWorld, new(normal, 2), 4, 4, 0);
        using var context = new ReceiverContext("ReadOpaqueReceiver");
        RenderGraphFrame frame = context.Graph.BeginFrame();
        ulong identity;
        try
        {
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(context.Uniforms), in parameters);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[context.Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed, context.Graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));
        ReceiverResult[] results = context.Result.Values;
        for (int index = 0; index < results.Length; index++)
        {
            float x = ((index % 4 + .5f) / 4f * 2 - 1) / projection.M11;
            float y = (1 - (index / 4 + .5f) / 4f * 2) / projection.M22;
            Vector3 ray = new(x, y, -1);
            Vector3 expectedPosition = ray * (-2f / Vector3.Dot(normal, ray));
            Assert.InRange(Vector3.Distance(expectedPosition, results[index].Position), 0, .0002f);
            Assert.InRange(Vector3.Distance(Vector3.Normalize(normal), results[index].Normal), 0, .0002f);
            Assert.Equal(0u, results[index].FirstPerson);
        }
    }

    [Fact]
    public void InterleavedGpuLightTypesMapToTheirFirstRegularShadowProjection()
    {
        VirtualShadowView[] views =
        [
            new() { LightIndex = 0, LightKind = VirtualShadowLightKind.Directional, ClipmapLevel = 5, FirstClipmapLevel = 5 },
            new() { LightIndex = 0, LightKind = VirtualShadowLightKind.Directional, ClipmapLevel = 6, FirstClipmapLevel = 5 },
            new() { LightIndex = 1, LightKind = VirtualShadowLightKind.Spot },
            new() { LightIndex = 2, LightKind = VirtualShadowLightKind.Point, FaceIndex = 0 },
            new() { LightIndex = 2, LightKind = VirtualShadowLightKind.Point, FaceIndex = 1 },
            new() { LightIndex = 0, LightKind = VirtualShadowLightKind.Directional, ClipmapLevel = 8, FirstClipmapLevel = 8,
                Flags = VirtualShadowViewFlags.FirstPersonShadow },
        ];
        var indices = new uint[3];

        VirtualShadowLightViews.WriteIndices(views, indices);

        Assert.Equal(new uint[] { 0, 2, 3 }, indices);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 160)]
    private readonly record struct Parameters(Matrix4x4 Projection, Matrix4x4 ClipToWorld,
        Vector4 Plane, uint Width, uint Height, uint FirstPerson);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
    private readonly record struct ReceiverResult(Vector3 Position, uint FirstPerson, Vector3 Normal, float Depth);

    private sealed class ReceiverContext : IDisposable
    {
        private readonly BinningTestDevice _graphics = new();
        private readonly ShaderLibrary _shaders;
        private readonly GraphBufferId _results;
        private readonly GraphTextureId _depth;
        private readonly GraphPassId _writeDepth, _readReceiver;
        private readonly GraphReadbackId _readback;
        private readonly GraphBufferCbvId _uniformView;
        private readonly GraphBufferUavId _resultView;
        private readonly GraphTextureUavId _depthWrite;
        private readonly GraphTextureSrvId _depthRead;
        internal global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
        internal GraphBufferId Uniforms { get; }
        internal ReceiverReadback Result { get; } = new();

        internal ReceiverContext(string entry)
        {
            string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../assets/Shaders"));
            IGlobalSession global = Shader.CreateSlangGlobalSession();
            SessionDesc compilation = TestShaderLoading.CreateSessionDescription(global);
            compilation.SearchPaths = [directory];
            _shaders = new(global, compilation);
            Shader shader = _shaders.CreateAsync("Visibility shadow receivers", "vsm_visibility_checks.slang", ShaderSource)
                .AsTask().GetAwaiter().GetResult();
            LinkedComputePipeline write = LinkedComputePipeline.Create(_graphics.Pipelines, shader, "WriteDepthPlane", "Write receiver plane depth");
            LinkedComputePipeline read = LinkedComputePipeline.Create(_graphics.Pipelines, shader, entry, "Read visibility shadow receivers");
            Graph = new(_graphics.Backend, _graphics.Device, [_graphics.Backend.GetQueue(_graphics.Device, QueueType.Graphics)]);
            RenderGraphEdit edit = Graph.BeginEdit();
            try
            {
                uint alignment = _graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
                uint bytes = (160u + alignment - 1u) / alignment * alignment;
                Uniforms = edit.CreateBuffer(new(bytes, BufferUsages.Constant), MemoryType.Upload);
                _uniformView = edit.CreateBufferCbv(Uniforms);
                _results = edit.CreatePersistentBuffer(new(16 * 32, BufferUsages.ShaderWrite | BufferUsages.CopySource));
                _resultView = edit.CreateBufferUav(_results, structureStride: 32);
                _depth = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
                    Format.R32Float, TextureUsages.Sampled | TextureUsages.Storage));
                _depthWrite = edit.CreateTextureUav(_depth, format: Format.R32Float, dimension: TextureViewDimension.Texture2DArray);
                _depthRead = edit.CreateTextureSrv(_depth, format: Format.R32Float, dimension: TextureViewDimension.Texture2DArray);
                var bindings = new Dictionary<string, GraphParameterResourceBinding>
                {
                    ["ReceiverParameters"] = GraphParameterResourceBinding.ConstantBuffer(_uniformView, PipelineSync.ComputeShading),
                    ["ReceiverDepthWrite"] = GraphParameterResourceBinding.StorageTexture(_depthWrite,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                    ["ReceiverDepthRead"] = GraphParameterResourceBinding.SampledTexture(_depthRead, PipelineSync.ComputeShading),
                    ["ReceiverResults"] = GraphParameterResourceBinding.WritableBuffer(_resultView,
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
                };
                _writeDepth = Add(ref edit, shader, write, bindings);
                _readReceiver = Add(ref edit, shader, read, bindings);
                edit.OrderAfter(_readReceiver, _writeDepth);
                _readback = edit.AddReadback(_results, BufferRange.Whole, Result, "Visibility receiver results");
                edit.OrderAfter(_readback, _readReceiver);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }

        private static GraphPassId Add(ref RenderGraphEdit edit, Shader shader, LinkedComputePipeline pipeline,
            Dictionary<string, GraphParameterResourceBinding> bindings)
        {
            GraphPassId passId = edit.AddComputeFramePass(pipeline.EntryPoint.Name, PassQueueSelection.AnyCompatible(),
                new DispatchArguments(1, 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
            PassDefinition pass = edit.GetPass(passId);
            pass.SetPipeline(pipeline.Pipeline);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline,
                (name, _) => bindings.TryGetValue(name, out GraphParameterResourceBinding value) ? value : null)) pass.Bind(binding);
            return passId;
        }

        public void Dispose()
        {
            RenderGraphEdit edit = Graph.BeginEdit();
            try
            {
                edit.Remove(_readback);
                edit.Remove(_readReceiver);
                edit.Remove(_writeDepth);
                edit.Remove(_uniformView);
                edit.Remove(_resultView);
                edit.Remove(_depthRead);
                edit.Remove(_depthWrite);
                edit.Remove(Uniforms);
                edit.Remove(_results);
                edit.Remove(_depth);
                edit.Commit();
            }
            finally { edit.Dispose(); }
            Graph.Dispose();
            _shaders.Dispose();
            _graphics.Dispose();
        }
    }

    private sealed class ReceiverReadback : IGraphReadbackReceiver
    {
        internal ReceiverResult[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, ReceiverResult>(bytes).ToArray();
    }

    private const string ShaderSource = """
        #include "vsm_visibility_requests.slang"
        struct ReceiverTestParameters
        {
            float4x4 Projection;
            float4x4 ClipToWorld;
            float4 Plane;
            uint Width, Height, FirstPerson, Padding;
        };
        ConstantBuffer<ReceiverTestParameters> ReceiverParameters;
        RWTexture2DArray<float> ReceiverDepthWrite;
        Texture2DArray<float> ReceiverDepthRead;
        struct ReceiverResult
        {
            float3 Position; uint FirstPerson;
            float3 Normal; float Depth;
        };
        RWStructuredBuffer<ReceiverResult> ReceiverResults;
        StructuredBuffer<uint> ReceiverShadowViewIndices;
        [shader("compute")] [numthreads(64,1,1)]
        void WriteDepthPlane(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= ReceiverParameters.Width * ReceiverParameters.Height) return;
            uint2 pixel = uint2(id.x % ReceiverParameters.Width, id.x / ReceiverParameters.Width);
            float2 ndc = (float2(pixel) + .5) / float2(ReceiverParameters.Width, ReceiverParameters.Height) * float2(2,-2) + float2(-1,1);
            float3 ray = float3(ndc.x / ReceiverParameters.Projection[0][0], ndc.y / ReceiverParameters.Projection[1][1], -1);
            float distance = -ReceiverParameters.Plane.w / dot(ReceiverParameters.Plane.xyz, ray);
            float4 clip = mul(float4(ray * distance, 1), ReceiverParameters.Projection);
            ReceiverDepthWrite[uint3(pixel,0)] = clip.z / clip.w;
        }
        void SaveReceiver(uint index, SurfaceReceiver value)
        {
            ReceiverResult result;
            result.Position = value.Position;
            result.Normal = value.GeometricNormal;
            result.Depth = value.Depth;
            result.FirstPerson = (value.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u ? 1u : 0u;
            ReceiverResults[index] = result;
        }
        [shader("compute")] [numthreads(64,1,1)]
        void ReadOpaqueReceiver(uint3 id : SV_DispatchThreadID)
        {
            uint2 size = uint2(ReceiverParameters.Width, ReceiverParameters.Height);
            if (id.x >= size.x * size.y) return;
            uint3 pixel = uint3(id.x % size.x, id.x / size.x, 0);
            SaveReceiver(id.x, ReconstructVirtualShadowOpaqueReceiver(ReceiverDepthRead, pixel, size,
                ReceiverParameters.ClipToWorld, ReceiverParameters.FirstPerson != 0u));
        }
        [shader("compute")] [numthreads(64,1,1)]
        void RequestOpaquePages(uint3 id : SV_DispatchThreadID)
        {
            uint2 size = uint2(ReceiverParameters.Width, ReceiverParameters.Height);
            if (id.x >= size.x * size.y) return;
            uint3 pixel = uint3(id.x % size.x, id.x / size.x, 0);
            RequestVirtualShadowOpaqueVisibility(ReceiverDepthRead, pixel, size, ReceiverParameters.ClipToWorld,
                ReceiverParameters.FirstPerson != 0u, ReceiverShadowViewIndices);
        }
        """;
}
