using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class TextureDisplacementTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void Bounds_enclose_sampled_surfaces_and_converge_across_bilinear_cells_and_repeat_seams()
    {
        const int caseCount = 48, depthCount = 7, count = caseCount * depthCount;
        const string source = """
            #include "texture_displacement.slang"
            #include "cluster_view.slang"
            Texture2D<float> HeightMap;
            Texture2D<float2> HeightBounds;
            SamplerState HeightSampler;
            RWStructuredBuffer<float4> Results;
            float3 ContinuousPosition(TextureDisplacement d, Triangle t, float3 b)
            {
                SurfaceEval linear = EvalLinearSurface(t, b);
                return linear.Position + linear.Normal * d.sample(d.uv(t,b)).x;
            }
            [shader("compute")]
            [numthreads(32, 1, 1)]
            void CheckDisplacement(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= 336u) return;
                uint test = id.x / 7u, depth = id.x % 7u;
                float size = 0.7 * exp2(-float(depth));
                Triangle t = {};
                t.Position1 = float3(1,0,0); t.Position2 = float3(0,1,0);
                t.Normal0 = normalize(float3(-0.4,0.2,1));
                t.Normal1 = normalize(float3(0.4,-0.3,1));
                t.Normal2 = normalize(float3(0.1,0.4,1));
                t.TexCoord0 = float2(float(test % 8u) / 7.0 - 0.3, float(test / 8u) / 5.0 - 0.2);
                t.TexCoord1 = t.TexCoord0 + float2(0.71,0.13);
                t.TexCoord2 = t.TexCoord0 + float2(-0.17,0.67);
                TextureDisplacement d = MakeTextureDisplacement(HeightMap, HeightBounds, HeightSampler,
                    float2(0,1), (test & 1u) == 0u ? 0.7 : -0.7, 0.13);
                float3 b0 = float3(1,0,0), b1 = float3(1-size,size,0), b2 = float3(1-size,0,size);
                float3 p0 = d.position(t,b0), p1 = d.position(t,b1), p2 = d.position(t,b2);
                DomainErrorBounds error = d.errorBounds(t,b0,b1,b2,p0,p1,p2);
                float3 lo, hi;
                d.patchBounds(t,b0,b1,b2,lo,hi);
                float violation = -1e30, edgeViolation = -1e30, boundsViolation = -1e30;
                for (uint y = 0; y <= 16u; y++)
                for (uint x = 0; x <= 16u-y; x++)
                {
                    float3 local = float3(1.0-float(x+y)/16.0,float(x)/16.0,float(y)/16.0);
                    float3 b = b0*local.x+b1*local.y+b2*local.z;
                    float3 actual = d.position(t,b);
                    float residual = length(actual-(p0*local.x+p1*local.y+p2*local.z));
                    violation = max(violation,residual-error.Interior);
                    if (y == 0u) edgeViolation = max(edgeViolation,residual-error.Edge.x);
                    if (x+y == 16u) edgeViolation = max(edgeViolation,residual-error.Edge.y);
                    if (x == 0u) edgeViolation = max(edgeViolation,residual-error.Edge.z);
                    boundsViolation = max(boundsViolation,max(max(max(lo.x-actual.x,actual.x-hi.x),
                        max(lo.y-actual.y,actual.y-hi.y)),max(lo.z-actual.z,actual.z-hi.z)));
                }
                float3 middle = (b0+b1+b2)/3.0;
                float3 du = float3(-1,1,0)*0.0001, dv = float3(-1,0,1)*0.0001;
                float3 numericDu = (ContinuousPosition(d,t,middle+du)-ContinuousPosition(d,t,middle-du))/0.0002;
                float3 numericDv = (ContinuousPosition(d,t,middle+dv)-ContinuousPosition(d,t,middle-dv))/0.0002;
                SurfaceEval evaluated = d.eval(t,middle);
                float derivativeError = max(length(numericDu-evaluated.DpDu),length(numericDv-evaluated.DpDv));
                // A bilinear gradient is discontinuous at cell boundaries; compare only
                // where both centered differences stay inside the same bilinear cell.
                float2 cell = floor(d.uv(t,middle)*8.0-0.5);
                bool smooth = all(floor(d.uv(t,middle+du)*8.0-0.5)==cell) &&
                    all(floor(d.uv(t,middle-du)*8.0-0.5)==cell) &&
                    all(floor(d.uv(t,middle+dv)*8.0-0.5)==cell) &&
                    all(floor(d.uv(t,middle-dv)*8.0-0.5)==cell);
                Results[id.x*2u] = float4(violation,edgeViolation,boundsViolation,error.Interior);
                ClusterViewUniforms view = {};
                view.ScreenWidth = 800; view.ScreenHeight = 600;
                bool orthographic = test % 3u == 0u;
                view.ViewProj = orthographic
                    ? float4x4(0.3,0,0,0, 0,0.4,0,0, 0,0,0.1,0, 0.2,-0.3,0,1)
                    : float4x4(1.2,0,0,0.2, 0,0.8,0,0.3, 0.4,-0.3,-1,-1, 0.2,-0.3,-0.1,0);
                float3 center = float3(-1.0+float(test)*0.05,0.35,-5.0-float(depth));
                float projectedBound = ClusterSphereError(center,0.7,0.15,view);
                float projectionViolation = -1e30;
                for (uint direction = 0; direction < 64u; direction++)
                {
                    float3 radial = normalize(float3(sin(float(direction)*1.17),cos(float(direction)*0.83),sin(float(direction)*0.31)));
                    float3 shift = normalize(float3(cos(float(direction)*0.47),sin(float(direction)*1.37),cos(float(direction)*0.61)))*0.15;
                    float4 a = mul(float4(center+radial*0.7,1),view.ViewProj);
                    float4 b = mul(float4(center+radial*0.7+shift,1),view.ViewProj);
                    float2 delta = abs(a.xy/a.w-b.xy/b.w)*float2(400,300);
                    projectionViolation = max(projectionViolation,max(delta.x,delta.y)-projectedBound);
                }
                Results[id.x*2u+1u] = float4(smooth ? derivativeError : 0.0,
                    projectionViolation,orthographic ? abs(projectedBound-18.0) : 0.0,
                    length(evaluated.Position - d.position(t,middle)));
            }
            """;
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(
            "displacement_bounds", source, [new("CheckDisplacement", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device, new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(4096, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            map.Bytes.Clear();
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                float value = ((x * 5 + y * 3) % 11) / 10f;
                MemoryMarshal.Write(map.Bytes.Slice(y * 256 + x * 4), in value);
                float high = 1;
                MemoryMarshal.Write(map.Bytes.Slice(2048 + y * 256 + x * 8 + 4), in high);
            }
            map.Flush(new BufferRange(0, 4096));
        }
        using Texture heights = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            8, 8, 1, 1, 1, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        using Texture bounds = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            8, 8, 1, 1, 1, 1, Format.R32G32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv heightView = backend.CreateTextureSrv(device, new TextureSrvDesc(heights, textureRange, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv boundsView = backend.CreateTextureSrv(device, new TextureSrvDesc(bounds, textureRange, Format.R32G32Float, TextureViewDimension.Texture2D));
        using Sampler sampler = backend.CreateSampler(device, new SamplerDesc(
            FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat));
        const ulong bytes = count * 32;
        using Buffer output = backend.CreateBuffer(device, new BufferDesc(bytes, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
        using BufferUav outputView = backend.CreateBufferUav(device, new BufferUavDesc(output, BufferRange.Whole, StructureStride: 16));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(bytes, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(32, 8, 32));
        foreach (Texture texture in new[] { heights, bounds })
            backend.Barrier(context, new TextureBarrier(texture, textureRange, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(context, new BufferTextureCopy(upload, 0, 256, 8, heights, 0, 0, TextureAspects.Color, 0, 0, 0, 8, 8, 1));
        backend.CopyBufferToTexture(context, new BufferTextureCopy(upload, 2048, 256, 8, bounds, 0, 0, TextureAspects.Color, 0, 0, 0, 8, 8, 1));
        foreach (Texture texture in new[] { heights, bounds })
            backend.Barrier(context, new TextureBarrier(texture, textureRange, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new ParameterBlockBindings(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
            [ResourceBinding.Null(ResourceBindingType.BufferSrv), ResourceBinding.Null(ResourceBindingType.ConstantBuffer),
             ResourceBinding.SampledTexture(heightView), ResourceBinding.SampledTexture(boundsView), ResourceBinding.SampledWith(sampler), ResourceBinding.WritableBuffer(outputView)], []));
        backend.Dispatch(context, new DispatchArguments((count + 31u) / 32u, 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, bytes));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(new BufferRange(0, bytes));
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(result.Bytes);
        for (int index = 0; index < count; index++)
        {
            for (int component = 0; component < 3; component++)
                Assert.True(float.IsFinite(values[index * 8 + component]) && values[index * 8 + component] <= 0.00001f,
                    $"Case {index / depthCount}, depth {index % depthCount}, bound {component}: {values[index * 8 + component]}");
            Assert.InRange(values[index * 8 + 4], 0, 0.006f);
            Assert.True(values[index * 8 + 5] <= 0.0001f, $"Projection bound failed for case {index}: {values[index * 8 + 5]}");
            Assert.InRange(values[index * 8 + 6], 0, 0.0001f);
            Assert.InRange(values[index * 8 + 7], 0, 0.00001f);
        }
        for (int test = 0; test < caseCount; test++)
            Assert.True(values[(test * depthCount + depthCount - 1) * 8 + 3] < values[test * depthCount * 8 + 3] * 0.15f,
                $"Case {test} did not converge after six subdivisions.");
    }

    [Fact]
    public async Task BoundsTextureDisplacementDrawsTheSamePixelsAsSourceVertices()
    {
        const int size = 64, pitch = size * 4;
        float[] samples = Enumerable.Range(0, 64).Select(index => ((index % 8 * 5 + index / 8 * 3) % 11) / 10f).ToArray();
        using TextureAsset boundsAsset = DisplacementBoundsTexture.Create(Guid.NewGuid(), "draw bounds", 8, 8, samples, out _);
        SomeEngine.Resources.Texture boundsPixels = await boundsAsset.GetBlock<TextureBlock>().CreateAsync(firstMip: 0);
        List<int> indices = [];
        for (int y = 0; y < 7; y++)
        for (int x = 0; x < 7; x++)
        {
            int a = y * 8 + x, b = a + 1, c = a + 8, d = c + 1;
            indices.AddRange([a, c, b, b, c, d]);
        }
        Vector4[] sourceVertices = indices.Select(index =>
        {
            float u = (index % 8 + .5f) / 8, v = (index / 8 + .5f) / 8;
            return new Vector4(u * 1.5f - .75f, v * 1.5f - .75f, u, v);
        }).ToArray();
        Vector3[] referenceVertices = indices.Select((index, vertex) =>
            new Vector3(sourceVertices[vertex].X, sourceVertices[vertex].Y + samples[index] * .25f, .5f)).ToArray();
        const string source = """
            #include "texture_displacement.slang"
            StructuredBuffer<float4> SourceVertices;
            StructuredBuffer<float3> ReferenceVertices;
            Texture2D<float> HeightMap;
            Texture2D<float2> HeightBounds;
            SamplerState HeightSampler;
            struct VertexOutput { float4 position : SV_Position; nointerpolation uint valid : TEXCOORD0; };
            [shader("vertex")]
            VertexOutput DisplacedVertex(uint vertex : SV_VertexID)
            {
                uint first = vertex / 3u * 3u;
                float4 a = SourceVertices[first], b = SourceVertices[first + 1u], c = SourceVertices[first + 2u];
                Triangle t = {};
                t.Position0 = float3(a.xy, .5); t.Position1 = float3(b.xy, .5); t.Position2 = float3(c.xy, .5);
                t.Normal0 = t.Normal1 = t.Normal2 = float3(0,1,0);
                t.TexCoord0 = a.zw; t.TexCoord1 = b.zw; t.TexCoord2 = c.zw;
                TextureDisplacement surface = MakeTextureDisplacement(HeightMap, HeightBounds, HeightSampler, float2(0,1), .25, 0);
                float3 bary = vertex % 3u == 0u ? float3(1,0,0) : vertex % 3u == 1u ? float3(0,1,0) : float3(0,0,1);
                float3 position = surface.position(t, bary), lo, hi;
                surface.patchBounds(t, float3(1,0,0), float3(0,1,0), float3(0,0,1), lo, hi);
                VertexOutput output;
                output.position = float4(position,1);
                output.valid = all(position >= lo - 1e-5) && all(position <= hi + 1e-5) ? 1u : 0u;
                return output;
            }
            [shader("vertex")]
            VertexOutput ReferenceVertex(uint vertex : SV_VertexID)
            {
                VertexOutput output;
                output.position = float4(ReferenceVertices[vertex],1);
                output.valid = 1u;
                return output;
            }
            [shader("fragment")]
            float4 Shade(VertexOutput input) : SV_Target0 { return input.valid != 0u ? float4(0,1,0,1) : float4(1,0,0,1); }
            """;
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("displacement_draw", source,
            [new("DisplacedVertex", SlangStage.Vertex), new("ReferenceVertex", SlangStage.Vertex), new("Shade", SlangStage.Fragment)],
            "sm_6_0", ShaderDirectory());
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer sourceBuffer = backend.CreateBuffer(device, new((ulong)sourceVertices.Length * 16, BufferUsages.ShaderRead), MemoryType.Upload);
        using Buffer referenceBuffer = backend.CreateBuffer(device, new((ulong)referenceVertices.Length * 12, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer map = backend.Map(sourceBuffer, MapType.Write, BufferRange.Whole))
        { MemoryMarshal.AsBytes(sourceVertices.AsSpan()).CopyTo(map.Bytes); map.Flush(map.Range); }
        using (MappedBuffer map = backend.Map(referenceBuffer, MapType.Write, BufferRange.Whole))
        { MemoryMarshal.AsBytes(referenceVertices.AsSpan()).CopyTo(map.Bytes); map.Flush(map.Range); }
        using BufferSrv sourceView = backend.CreateBufferSrv(device, new(sourceBuffer, BufferRange.Whole, StructureStride: 16));
        using BufferSrv referenceView = backend.CreateBufferSrv(device, new(referenceBuffer, BufferRange.Whole, StructureStride: 12));
        using Buffer upload = backend.CreateBuffer(device, new(6144, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            map.Bytes.Clear();
            for (int y = 0; y < 8; y++)
                MemoryMarshal.AsBytes(samples.AsSpan(y * 8, 8)).CopyTo(map.Bytes.Slice(y * 256));
            ulong offset = 2048;
            foreach (var tile in boundsPixels.MipTiles)
            {
                Assert.True(boundsPixels.TryGetTile(tile.ChunkKey, out ReadOnlyMemory<byte> mipBytes));
                for (uint y = 0; y < tile.Height; y++)
                    mipBytes.Span.Slice((int)(y * tile.RowPitch), (int)tile.Width * 8).CopyTo(map.Bytes.Slice((int)(offset + y * 256u)));
                offset += tile.Height * 256u;
            }
            map.Flush(map.Range);
        }
        using Texture heights = backend.CreateTexture(device, new(TextureDimension.Texture2D, 8, 8, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        using Texture bounds = backend.CreateTexture(device, new(TextureDimension.Texture2D, 8, 8, 1, boundsPixels.MipLevelCount, 1, 1,
            Format.R32G32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
        TextureSubresourceRange heightRange = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange boundsRange = new(0, boundsPixels.MipLevelCount, 0, 1, TextureAspects.Color);
        using TextureSrv heightView = backend.CreateTextureSrv(device, new(heights, heightRange, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv boundsView = backend.CreateTextureSrv(device, new(bounds, boundsRange, Format.R32G32Float, TextureViewDimension.Texture2D));
        using Sampler sampler = backend.CreateSampler(device, new(FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat));
        using Texture actual = backend.CreateTexture(device, new(TextureDimension.Texture2D, size, size, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using Texture expected = backend.CreateTexture(device, new(TextureDimension.Texture2D, size, size, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        using ColorAttachmentView actualView = backend.CreateColorAttachmentView(device, new(actual, heightRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using ColorAttachmentView expectedView = backend.CreateColorAttachmentView(device, new(expected, heightRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer actualPixels = backend.CreateBuffer(device, new(pitch * size, BufferUsages.CopyDestination), MemoryType.Readback);
        using Buffer expectedPixels = backend.CreateBuffer(device, new(pitch * size, BufferUsages.CopyDestination), MemoryType.Readback);
        Pipeline CreateDrawingPipeline(int entry) => backend.CreateGraphicsPipeline(device, new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(entry), shader.GetEntryCode(entry), shader.GetEntryPoint(2), shader.GetEntryCode(2), [], [], PrimitiveTopology.TriangleList,
            StripCut.Disabled, new RasterizerState(Cull: CullType.None), new MultisampleState(SampleCount: 1),
            new DepthStencilState(), new BlendState([new(Enabled: false, WriteMask: ColorWriteMasks.All)]), new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null)));
        using Pipeline displaced = CreateDrawingPipeline(0);
        using Pipeline reference = CreateDrawingPipeline(1);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
        (Texture Texture, TextureSubresourceRange Range)[] inputs = [(heights, heightRange), (bounds, boundsRange)];
        foreach (var item in inputs)
            backend.Barrier(context, new TextureBarrier(item.Texture, item.Range, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(context, new(upload, 0, 256, 8, heights, 0, 0, TextureAspects.Color, 0, 0, 0, 8, 8, 1));
        ulong mipOffset = 2048;
        foreach (var tile in boundsPixels.MipTiles)
        {
            backend.CopyBufferToTexture(context, new(upload, mipOffset, 256, tile.Height, bounds, tile.MipLevel, 0,
                TextureAspects.Color, 0, 0, 0, tile.Width, tile.Height, 1));
            mipOffset += tile.Height * 256u;
        }
        foreach (var item in inputs)
            backend.Barrier(context, new TextureBarrier(item.Texture, item.Range, PipelineSync.Copy, PipelineSync.VertexShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
        (Texture Target, ColorAttachmentView View, Pipeline Pipeline, Buffer Pixels)[] outputs =
            [(actual, actualView, displaced, actualPixels), (expected, expectedView, reference, expectedPixels)];
        foreach (var output in outputs)
        {
            backend.Barrier(context, new TextureBarrier(output.Target, heightRange, PipelineSync.None, PipelineSync.RenderTarget,
                ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
            backend.SetPipeline(context, output.Pipeline);
            backend.SetViewports(context, [new(0, 0, size, size)]);
            backend.SetScissors(context, [new(0, 0, size, size)]);
            backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                [ResourceBinding.ReadOnlyBuffer(sourceView), ResourceBinding.ReadOnlyBuffer(referenceView),
                 ResourceBinding.SampledTexture(heightView), ResourceBinding.SampledTexture(boundsView), ResourceBinding.SampledWith(sampler)], []));
            backend.BeginRendering(context, new([new(output.View, LoadType.Clear, StoreType.Store, Vector4.Zero)], null, size, size));
            backend.Draw(context, new((uint)sourceVertices.Length, 1, 0, 0));
            backend.EndRendering(context);
            backend.Barrier(context, new TextureBarrier(output.Target, heightRange, PipelineSync.RenderTarget, PipelineSync.Copy,
                ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
            backend.CopyTextureToBuffer(context, new(output.Pixels, 0, pitch, size, output.Target, 0, 0, TextureAspects.Color, 0, 0, 0, size, size, 1));
        }
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
        using MappedBuffer actualMap = backend.Map(actualPixels, MapType.Read, BufferRange.Whole);
        using MappedBuffer expectedMap = backend.Map(expectedPixels, MapType.Read, BufferRange.Whole);
        actualMap.Invalidate(actualMap.Range);
        expectedMap.Invalidate(expectedMap.Range);
        Assert.Equal(expectedMap.Bytes.ToArray(), actualMap.Bytes.ToArray());
        int covered = 0;
        for (int pixel = 0; pixel < size * size; pixel++) if (actualMap.Bytes[pixel * 4 + 1] == 255) covered++;
        Assert.True(covered > 100);
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "assets", "Shaders");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Could not find displacement shaders.");
    }
}
