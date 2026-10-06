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
