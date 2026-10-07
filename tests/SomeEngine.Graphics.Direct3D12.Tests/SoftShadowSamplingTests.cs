using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class SoftShadowSamplingTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void AFiniteDirectionalEmitterProducesAPenumbraAroundADepthEdge()
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            ClipmapLevelCount = 1, MipLevelCount = 1, ProjectionDepthScale = -1,
            WorldUnitsPerCentimeter = 1f, LightDirection = Vector3.UnitZ,
            SourceAngleRadians = .2f, CacheGeneration = 7,
        }];
        uint[] depth = Enumerable.Range(0, 128 * 128)
            .Select(pixel => pixel % 128 < 64 ? BitConverter.SingleToUInt32Bits(.75f) : 0u).ToArray();
        uint[][] observed = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_soft_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(32,1,1)]
            void ReadSoftDepthEdge(uint index : SV_DispatchThreadID)
            {
                float3 receiver = float3((float(index) - 15.5) / 64.0, 0.0, .75);
                Visibility[index] = SampleVirtualShadowDirectionalSoft(Views, Pages, Atlas, 0u, 1u,
                    receiver, float3(0,0,-1), 0.0, 0.0, 4u, 8u, 8.0, uint2(index,64), 0u).Visibility;
            }
            """, "ReadSoftDepthEdge", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(), ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (new uint[] { 0xc0000000u }, ResourceBindingType.BufferSrv, 4),
             (new uint[32], ResourceBindingType.BufferUav, 4)],
            new SampledImage(128, 128, depth, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        float[] visibility = observed[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.Equal(0f, visibility[0]);
        Assert.Equal(1f, visibility[^1]);
        Assert.Contains(visibility, value => value > 0f && value < 1f);
    }
}
