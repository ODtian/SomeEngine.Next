using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class RasterSampleBoundsTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(false, 512u)]
    [InlineData(true, 512u)]
    [InlineData(false, 16384u)]
    [InlineData(true, 16384u)]
    public void EveryRasterizedTriangleSampleSurvivesItsBounds(bool fourBitSnapping, uint screenSize)
    {
        const int size = 512, triangleCount = 4096, pitch = size * 4;
        float origin = screenSize == size ? 0 : screenSize / 2;
        float[] radii = [.0001f, .002f, .02f, .05f, .1f, .25f, .49f, .75f, 1.5f];
        float[] offsets = [0, .25f, .5f, .75f, .5f - 1f / 512, .5f + 1f / 512,
            .5f - 1f / 32, .5f + 1f / 32];
        var vertices = new Vector4[triangleCount * 3];
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            float x = triangle % 64 * 8 + 3 + offsets[triangle / 81 % offsets.Length];
            float y = triangle / 64 * 8 + 3 + offsets[triangle / 17 % offsets.Length];
            float width = radii[triangle % radii.Length], height = radii[triangle / 9 % radii.Length];
            vertices[triangle * 3] = Clip(x - width, y - height, 1);
            vertices[triangle * 3 + 1] = Clip(x + width, y - height, triangle % 3 == 0 ? .5f : 1);
            vertices[triangle * 3 + 2] = Clip(x, y + height, triangle % 3 == 0 ? 2 : 1);
            if ((triangle & 1) != 0)
                (vertices[triangle * 3 + 1], vertices[triangle * 3 + 2]) =
                    (vertices[triangle * 3 + 2], vertices[triangle * 3 + 1]);
        }
        Vector4 Clip(float x, float y, float w) =>
            new(((x + origin) / screenSize * 2 - 1) * w,
                (1 - (y + origin) / screenSize * 2) * w, .5f * w, w);

        string source = $"#define SAMPLE_SCREEN_SIZE {screenSize}\n#define FOUR_BIT_SNAPPING " +
            (fourBitSnapping ? "1\n" : "0\n") + """
            #include "raster_sample_bounds.slang"
            StructuredBuffer<float4> Vertices;
            struct VertexOutput
            {
                float4 position : SV_Position;
                nointerpolation float4 bounds : TEXCOORD0;
            };
            float2 Uv(float4 vertex) { return vertex.xy / vertex.w * float2(.5, -.5) + .5; }
            [shader("vertex")]
            VertexOutput DrawTriangle(uint vertex : SV_VertexID)
            {
                uint first = (vertex / 3u) * 3u;
                float2 a = Uv(Vertices[first]), b = Uv(Vertices[first + 1u]), c = Uv(Vertices[first + 2u]);
                VertexOutput output;
                output.bounds = float4(min(a, min(b, c)), max(a, max(b, c)));
                output.position = Vertices[vertex];
            #if FOUR_BIT_SNAPPING
                float2 pixel = floor(Uv(output.position) * float(SAMPLE_SCREEN_SIZE) * 16.0 + .5) / 16.0;
                output.position.xy = (pixel / float(SAMPLE_SCREEN_SIZE) * float2(2, -2) + float2(-1, 1)) * output.position.w;
            #endif
                return output;
            }
            [shader("fragment")]
            float4 CheckCoverage(VertexOutput input) : SV_Target0
            {
                return RasterBoundsCoverPixelCenters(input.bounds.xy, input.bounds.zw, uint2(SAMPLE_SCREEN_SIZE, SAMPLE_SCREEN_SIZE))
                    ? float4(0, 1, 0, 1) : float4(1, 0, 0, 1);
            }
            """;
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(
            "sample_coverage", source,
            [new("DrawTriangle", SlangStage.Vertex), new("CheckCoverage", SlangStage.Fragment)],
            "sm_6_0", ShaderDirectory());
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer positions = backend.CreateBuffer(device,
            new BufferDesc((ulong)vertices.Length * 16, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(positions, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(vertices.AsSpan()).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv positionsView = backend.CreateBufferSrv(device, new(positions, BufferRange.Whole, StructureStride: 16));
        using Texture target = backend.CreateTexture(device, new(TextureDimension.Texture2D, size, size, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(device,
            new(target, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device, new(pitch * size, BufferUsages.CopyDestination), MemoryType.Readback);
        using Pipeline pipeline = backend.CreateGraphicsPipeline(device, new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0), shader.GetEntryPoint(1), shader.GetEntryCode(1), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(SampleCount: 1),
            new DepthStencilState(), new BlendState([new(Enabled: false, WriteMask: ColorWriteMasks.All)]),
            new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null)));
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(target, range, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new(-origin, -origin, screenSize, screenSize)]);
        backend.SetScissors(context, [new(0, 0, size, size)]);
        backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            [ResourceBinding.ReadOnlyBuffer(positionsView)], []));
        backend.BeginRendering(context, new([new(targetView, LoadType.Clear, StoreType.Store, Vector4.Zero)], null, size, size));
        backend.Draw(context, new((uint)vertices.Length, 1, 0, 0));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(target, range, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new(readback, 0, pitch, size, target, 0, 0,
            TextureAspects.Color, 0, 0, 0, size, size, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        int covered = 0;
        for (int pixel = 0; pixel < size * size; pixel++)
        {
            Assert.Equal(0, result.Bytes[pixel * 4]);
            if (result.Bytes[pixel * 4 + 1] != 0) covered++;
        }
        Assert.True(covered > 100, "The hardware rasterizer must exercise covered samples.");
    }

    [Theory]
    [InlineData(128u)]
    [InlineData(1024u)]
    [InlineData(16384u)]
    public void BoundsBetweenCentersAreRejectedButSnappingMarginsSurvive(uint size)
    {
        uint[] result = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "raster_sample_bounds.slang"
            StructuredBuffer<uint> Size;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void CheckBounds()
            {
                float screen = float(Size[0]), origin = screen / 2.0;
                Result[0] = RasterBoundsCoverPixelCenters((origin + .1).xx / screen, (origin + .2).xx / screen, Size[0].xx);
                Result[1] = RasterBoundsCoverPixelCenters((origin + .48).xx / screen, (origin + .52).xx / screen, Size[0].xx);
                Result[2] = RasterBoundsCoverPixelCenters((origin + .44).xx / screen, (origin + .45).xx / screen, Size[0].xx);
                Result[3] = RasterBoundsCoverPixelCenters(float2(origin + .1, 0) / screen,
                    float2(origin + .2, screen) / screen, Size[0].xx);
            }
            """, "CheckBounds", new(1, 1, 1),
            [([size], ResourceBindingType.BufferSrv, 4), (new uint[4], ResourceBindingType.BufferUav, 4)]));
        Assert.Equal([0u, 1u, 1u, 0u], result);
    }

    [Theory]
    [InlineData(16u)]
    [InlineData(256u)]
    [InlineData(1024u)]
    public void GeometryProjectionDoesNotGrowWithHiZTexelSize(uint size)
    {
        uint[] result = Assert.Single(new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "cluster_view.slang"
            #include "cluster_hiz_shared.slang"
            StructuredBuffer<uint> Size;
            RWStructuredBuffer<float4> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ProjectBounds()
            {
                ClusterViewUniforms view = {};
                view.ViewProj = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1);
                view.ScreenWidth = view.ScreenHeight = 1024;
                view.HiZInvSize = (1.0 / float(Size[0])).xx;
                view.HiZMipCount = 1u;
                float2 low, high;
                float depth;
                uint mip;
                BuildWorldAabbScreenBoundsAndNearDepth(float3(-.25, -.25, .5), float3(-.2499, -.2499, .6),
                    false, view, low, high, depth, mip);
                Result[0] = float4(low, high);
            }
            """, "ProjectBounds", new(1, 1, 1),
            [([size], ResourceBindingType.BufferSrv, 4), (new uint[4], ResourceBindingType.BufferUav, 16)]));
        Vector4 bounds = MemoryMarshal.Cast<uint, Vector4>(result)[0];
        Assert.InRange(Vector4.Distance(bounds, new(.375f, .62495f, .37505f, .625f)), 0, 2e-7f);
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "assets", "Shaders");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Engine shader library was not found.");
    }
}
