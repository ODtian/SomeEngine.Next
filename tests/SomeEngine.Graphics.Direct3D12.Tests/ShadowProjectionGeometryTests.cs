using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowProjectionGeometryTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void LocalEmitterRadiusProducesTheKnownHalfPlanePenumbra()
    {
        DirectoryInfo sourceDirectory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(sourceDirectory.FullName, "assets/Noise/shadow_blue_noise.rgba8")))
            sourceDirectory = sourceDirectory.Parent ?? throw new DirectoryNotFoundException();
        uint[] noise = MemoryMarshal.Cast<byte, uint>(File.ReadAllBytes(
            Path.Combine(sourceDirectory.FullName, "assets/Noise/shadow_blue_noise.rgba8"))).ToArray();
        const int resolution = 1024, pageSize = 128, pagesPerRow = 8;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10);
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = view * projection, VirtualResolution = resolution, PageSize = pageSize,
            AtlasSize = resolution, PhysicalPagesPerRow = pagesPerRow, MaxPhysicalPages = 64,
            CacheGeneration = 7, MipLevelCount = 1, LightKind = VirtualShadowLightKind.Spot,
            WorldUnitsPerCentimeter = 1, SourceRadius = 0, ProjectionDepthScale = projection.M33,
            ProjectionDepthOffset = projection.M43, TexelDitherScale = 1, LightDirection = Vector3.UnitZ,
        }, new()
        {
            LightViewProjection = view * projection, VirtualResolution = resolution, PageSize = pageSize,
            AtlasSize = resolution, PhysicalPagesPerRow = pagesPerRow, MaxPhysicalPages = 64,
            CacheGeneration = 7, MipLevelCount = 1, LightKind = VirtualShadowLightKind.Spot,
            WorldUnitsPerCentimeter = 1, SourceRadius = .01f, ProjectionDepthScale = projection.M33,
            ProjectionDepthOffset = projection.M43, TexelDitherScale = 1, LightDirection = Vector3.UnitZ,
        }];
        uint[] pages = new uint[64 * 4], depth = new uint[resolution * resolution];
        Vector4 plane = Vector4.Transform(new Vector4(0,0,.5f,1), view * projection);
        for (int page = 0; page < 64; page++)
        {
            pages[page * 4] = (uint)page + 1; pages[page * 4 + 1] = (uint)(page % pagesPerRow);
            pages[page * 4 + 2] = (uint)(page / pagesPerRow); pages[page * 4 + 3] = 7;
        }
        for (int y = 0; y < resolution; y++)
            for (int x = 0; x < resolution / 2; x++) depth[y * resolution + x] = BitConverter.SingleToUInt32Bits(1 - plane.Z / plane.W);
        uint[][] actual = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #define VSM_BLUE_NOISE_TEXTURE 1
            Texture2D<float4> VirtualShadowBlueNoise;
            #include "vsm_projection.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float3> Result;
            [shader("compute")]
            [numthreads(2,1,1)]
            void ReadEmitterPenumbra(uint3 thread : SV_DispatchThreadID)
            {
                VirtualShadowRaySettings settings = (VirtualShadowRaySettings)0;
                settings.RayCount = 128u; settings.SamplesPerRay = 8u; settings.MaxRayAngle = .03;
                settings.ExtrapolateMaxSlope = .05; settings.MaxSlopeBias = 50.0;
                VirtualShadowProjectionResult projection = TraceVirtualShadowLocal(Views, Pages, Atlas,
                    thread.x, float3(-.0001,0,1), float3(0,0,-1), .001, 0.0,
                    settings, uint2(20,30), 0u, 0.0);
                Result[thread.x] = float3(projection.Valid ? 1.0 : 0.0, projection.Visibility, projection.OccluderDistance);
            }
            """, "ReadEmitterPenumbra", new(1,1,1),
            [(MemoryMarshal.Cast<VirtualShadowView,uint>(views).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv,16),
             (new uint[6], ResourceBindingType.BufferUav,12)], new SampledImage(resolution,resolution,depth,3),
             [new SampledImage(64,4096,noise,0,Format.R8G8B8A8UNorm)]);
        float[] values = actual[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        // A point emitter sees the open side. A .01-radius disc and the z=.5
        // half plane split the known emitter area almost in half at x=-.0001.
        Assert.Equal(new float[] { 1,1,0,1 }, new[] { values[0],values[1],values[2],values[3] });
        Assert.InRange(values[4], .35f, .7f);
        Assert.InRange(values[5], .49f, .51f);
    }

    [Fact]
    public void ZeroRayDirectionalProjectionUsesTheNativeGeometricSlopeAllowance()
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 1, PageSize = 1,
            AtlasSize = 1, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            CacheGeneration = 7, ClipmapLevelCount = 1, MipLevelCount = 1,
            WorldUnitsPerCentimeter = 1,
        }];
        uint[][] actual = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float2> Result;
            [shader("compute")]
            [numthreads(1,1,1)]
            void ReadInclinedReceiver()
            {
                float3 position = float3(-.5, 0, .65);
                float visibility;
                bool valid = TrySampleDirectionalVirtualShadowMap(Views, Pages, Atlas,
                    0u, 1u, 0.0, position, position + float3(1,0,-.2), position + float3(0,1,0),
                    1u, 1u, 0.0, 0.0, float3(.2,0,1), visibility);
                Result[0] = float2(valid ? 1.0 : 0.0, visibility);
            }
            """, "ReadInclinedReceiver", new(1,1,1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes), ([1u,0u,0u,7u], ResourceBindingType.BufferSrv, 16),
             (new uint[2], ResourceBindingType.BufferUav, 8)],
            new SampledImage(1,1,[BitConverter.SingleToUInt32Bits(.5f)],2));
        // Plane z=.65-.2*(x+.5) has forward UV slope-.4. Its1/4-texel
        // offset gives the native positive allowance2*.1=.2, covering depth.5.
        Assert.Equal(new float[] { 1, 1 }, actual[0].Select(BitConverter.UInt32BitsToSingle).ToArray());
    }
}
