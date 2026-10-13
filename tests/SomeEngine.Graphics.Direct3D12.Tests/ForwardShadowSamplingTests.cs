using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ForwardShadowSamplingTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void ForwardDirectionalShadowsMatchProjectedSoftVisibility()
    {
        uint[] expected = ExecuteDirectionalSamples(false);
        uint[] actual = ExecuteDirectionalSamples(true);
        Assert.Equal(expected, actual);
        Assert.Contains(Enumerable.Range(0, 32).Select(index => BitConverter.UInt32BitsToSingle(actual[index * 2])),
            value => value > 0 && value < 1);
    }

    private uint[] ExecuteDirectionalSamples(bool forward)
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            ClipmapLevelCount = 1, MipLevelCount = 1, ProjectionDepthScale = -1,
            WorldUnitsPerCentimeter = 1, LightDirection = Vector3.UnitZ,
            SourceAngleRadians = .2f, CacheGeneration = 7,
        }];
        VirtualShadowSamplingUniforms sampling = new()
        {
            ViewCount = 1, DirectionalLightCount = 1, DirectionalClipmapLevels = 1,
            FirstPersonViewOffset = uint.MaxValue, BlockerSampleCount = 4, FilterSampleCount = 8,
            MaxFilterRadiusTexels = 8, NormalBias = .025f, FrameIndex = 31,
            ReceiverProjection = new() { CotanHalfFieldOfView = 1 },
        };
        uint[] depth = Enumerable.Range(0, 128 * 128)
            .Select(pixel => pixel % 128 < 64 ? BitConverter.SingleToUInt32Bits(.75f) : 0u).ToArray();
        string source = """
            #include "vsm_forward_sampling.slang"
            Texture2DArray<float> TestDepth;
            RWStructuredBuffer<float2> Result;
            [shader("compute")]
            [numthreads(32,1,1)]
            void CompareVisibility(uint index : SV_DispatchThreadID)
            {
                SurfaceReceiver receiver = (SurfaceReceiver)0;
                receiver.Position = float3((float(index) - 15.5) / 64.0, 0, .75);
                receiver.ShadingNormal = normalize(float3(.2,0,-1));
                receiver.GeometricNormal = float3(0,0,-1);
                receiver.Depth = .75;
                receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
                GPULight light = (GPULight)0;
                light.Direction = float3(0,0,1);
                ClusterViewUniforms view = (ClusterViewUniforms)0;
                view.P11 = 1;
                #if FORWARD_SHADOW
                float visibility = EvaluateVirtualShadowForwardVisibility(uint3(index,64,0), 0u,
                    light, receiver, uint3(128,128,1), view, TestDepth);
                #else
                // The projection uses the authored geometric plane of this receiver.
                float bias = max(.02, .025 * length(receiver.Position));
                float visibility = SampleVirtualShadowDirectionalFilteredSoft(VirtualShadowViews,
                    VirtualShadowPageTable, VirtualShadowAtlas, VirtualShadowPrefilteredAtlas,
                    0u, 1u, receiver.Position + receiver.GeometricNormal * bias, receiver.GeometricNormal,
                    0.0, 4u, 8u, 8.0, uint2(index,64), 31u).Visibility;
                #endif
                Result[index] = float2(visibility, 0.0);
            }
            """;
        return ExecuteSamples("#define FORWARD_SHADOW " + (forward ? "1" : "0") + "\n" + source,
            views, sampling, depth);
    }

    // The same inline kernel exceeds the WARP wait budget; hardware parity remains explicit.
    [HardwareRowsTheory]
    [InlineData(true)]
    public void ForwardLocalShadowsMatchProjectedQuantizedVisibility(bool hardware)
    {
        uint[] expected = ExecuteLocalSamples(false, hardware);
        uint[] actual = ExecuteLocalSamples(true, hardware);
        Assert.Equal(expected, actual);
        Assert.Contains(Enumerable.Range(0, 32).Select(index => BitConverter.UInt32BitsToSingle(actual[index * 2])),
            value => value > 0 && value < 1);
    }

    private uint[] ExecuteLocalSamples(bool forward, bool hardware)
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10);
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = lightView * projection, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            CacheGeneration = 7, MipLevelCount = 1, LightKind = VirtualShadowLightKind.Spot,
            WorldUnitsPerCentimeter = 1, SourceRadius = .05f, LightDirection = Vector3.UnitZ,
            ProjectionDepthScale = projection.M33, ProjectionDepthOffset = projection.M43,
        }];
        VirtualShadowSamplingUniforms sampling = new()
        {
            ViewCount = 1, SpotLightCount = 1, SpotLightBufferOffset = 3, SpotViewOffset = 0,
            FirstPersonViewOffset = uint.MaxValue, BlockerSampleCount = 4, FilterSampleCount = 8,
            MaxFilterRadiusTexels = 8, NormalBias = .025f, FrameIndex = 63,
            ReceiverProjection = new()
            {
                CotanHalfFieldOfView = 1, WorldToClipW = Vector4.UnitW, ScreenPixelRadius = .001f,
            },
        };
        Vector4 plane = Vector4.Transform(new Vector4(0,0,.5f,1), lightView * projection);
        uint[] depth = Enumerable.Range(0, 128 * 128)
            .Select(pixel => pixel % 128 < 64 ? BitConverter.SingleToUInt32Bits(1 - plane.Z / plane.W) : 0u).ToArray();
        string source = """
            #include "vsm_forward_sampling.slang"
            Texture2DArray<float> TestDepth;
            RWStructuredBuffer<float2> Result;
            [shader("compute")]
            [numthreads(32,1,1)]
            void CompareVisibility(uint index : SV_DispatchThreadID)
            {
                SurfaceReceiver receiver = (SurfaceReceiver)0;
                receiver.Position = float3((float(index) - 15.5) / 128.0, 0, 1);
                receiver.ShadingNormal = normalize(float3(.2,0,-1));
                receiver.GeometricNormal = float3(0,0,-1);
                receiver.Depth = .75;
                receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING;
                GPULight light = (GPULight)0;
                light.Direction = float3(0,0,1);
                light.Range = 10;
                light.OuterConeCos = .5;
                ClusterViewUniforms view = (ClusterViewUniforms)0;
                view.P11 = 1;
                #if FORWARD_SHADOW
                float visibility = EvaluateVirtualShadowForwardVisibility(uint3(index,64,0), 3u,
                    light, receiver, uint3(128,128,1), view, TestDepth);
                #else
                float bias = max(.02, .025 * length(receiver.Position));
                float visibility = SampleVirtualShadowLocalSoft(VirtualShadowViews,
                    VirtualShadowPageTable, VirtualShadowAtlas, 0u,
                    receiver.Position + receiver.GeometricNormal * bias, receiver.GeometricNormal,
                    .001, 0.0, 4u, 8u, 8.0, uint2(index,64), 63u).Visibility;
                // Preserve the projected local buffer's four-bit value and blue-noise dither.
                float noise = VirtualShadowRayStepOffset(uint2(index,64), 63u);
                if (visibility > (1.0 / 15.0) * .25 && visibility < 1.0)
                    visibility = saturate(visibility + (noise - .5) * (1.0 / 15.0));
                visibility = float(uint(round(saturate(visibility) * 15.0))) / 15.0;
                #endif
                Result[index] = float2(visibility, 0.0);
            }
            """;
        return ExecuteSamples("#define FORWARD_SHADOW " + (forward ? "1" : "0") + "\n" + source,
            views, sampling, depth, hardware ? CreateHardwareDevice : null);
    }

    private static Device CreateHardwareDevice(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        AdapterInfo[] adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo adapter = adapters.First(static value => value.HardwareAccelerated);
        return backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            label: "Forward shadow parity hardware device"));
    }

    private uint[] ExecuteSamples(string source, VirtualShadowView[] views,
        VirtualShadowSamplingUniforms sampling, uint[] depth, Func<IGraphicsBackend, Device>? createDevice = null)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets/Noise/shadow_blue_noise.rgba8")))
            directory = directory.Parent ?? throw new DirectoryNotFoundException();
        uint[] noise = MemoryMarshal.Cast<byte, uint>(File.ReadAllBytes(
            Path.Combine(directory.FullName, "assets/Noise/shadow_blue_noise.rgba8"))).ToArray();
        VirtualShadowSamplingUniforms[] samplingData = [sampling];
        return new D3D12TestCompute(compiler, createDevice).ExecuteSourceImages(source, "CompareVisibility", new(1,1,1),
            [new("VirtualShadowViews", MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(),
                ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new("VirtualShadowPageTable", [VirtualShadowPageEntry.Encode(0, 1)], ResourceBindingType.BufferSrv, 4),
             new("VirtualShadowResidentPages", [0u], ResourceBindingType.BufferSrv, 4),
             new("VirtualShadowSampling", MemoryMarshal.Cast<VirtualShadowSamplingUniforms, uint>(samplingData).ToArray(),
                ResourceBindingType.ConstantBuffer, 0),
             new("VirtualShadowDynamicResolutionBias", [0u], ResourceBindingType.BufferSrv, 4),
             new("Result", new uint[64], ResourceBindingType.BufferUav, 8)],
            [new(128,8192,noise,0,Format.R8G8B8A8UNorm,Name: "VirtualShadowBlueNoise"),
             new(1,1,[uint.MaxValue],1,Name: "VirtualShadowReceiverMasks"),
             new(1,1,[BitConverter.SingleToUInt32Bits(1)],2,Format.R32Float,Name: "VirtualShadowPrefilteredAtlas"),
             new(128,128,depth,3,ViewDimension: TextureViewDimension.Texture2DArray,Name: "VirtualShadowAtlas"),
             new(1,1,[BitConverter.SingleToUInt32Bits(1)],4,Format.R32Float,
                ViewDimension: TextureViewDimension.Texture2DArray,Name: "TestDepth")])[0];
    }
}
