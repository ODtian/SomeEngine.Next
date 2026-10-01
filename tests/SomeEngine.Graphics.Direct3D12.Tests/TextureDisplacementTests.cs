using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class TextureDisplacementTests
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
        D3D12TestShader shader = D3D12TestShader.CompileWithSearchPaths(
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
