using System.Numerics;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowReceiverPositionTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void DirectionalRequestsCoverEveryVisibleSampleAtTheLightDepthBoundary()
    {
        var requests = new ShadowReceiverRequests(compiler);
        var view = new VirtualShadowView
        {
            LightViewProjection = new Matrix4x4(2,0,0,0, 0,2,0,0, 0,0,-1,0, 0,0,-.6f,1),
            VirtualResolution = 32, PageSize = 8, AtlasSize = 16, PhysicalPagesPerRow = 2,
            MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1, ClipmapLevelCount = 1,
            ClipmapLevel = 6, FirstClipmapLevel = 6, WorldUnitsPerCentimeter = .01f,
            LightDirection = new(0,0,-1),
        };
        uint[] pages = requests.Execute(view, """
            [shader("compute")]
            [numthreads(4,1,1)]
            void RequestPlaneSamples(uint index : SV_DispatchThreadID)
            {
                float3 position = float3((index & 1u) != 0u ? -.0625 : -.4375,
                    (index & 2u) != 0u ? .0625 : .4375, -.75);
                MarkVirtualShadowPages(position, 0u);
            }
            """, "RequestPlaneSamples", new(1,1,1));
        Assert.Equal(new uint[] { 1,1,1,1 }, requests.ReadCoverage(view,pages,
            [new(-.4375f,.4375f,-.75f),new(-.0625f,.4375f,-.75f),
             new(-.4375f,.0625f,-.75f),new(-.0625f,.0625f,-.75f)]));
    }

    [Fact]
    public void DepthSamplesKeepTheirShadowCoverageAcrossAnOccupiedTile()
    {
        var requests = new ShadowReceiverRequests(compiler);
        uint[] depth = Enumerable.Range(0,256).Select(pixel =>
            BitConverter.SingleToUInt32Bits(pixel % 16 < 8 && pixel / 16 < 8 ? .5f : 1f)).ToArray();
        var view = new VirtualShadowView
        {
            LightViewProjection = new Matrix4x4(2,0,0,0, 0,2,0,0, 0,0,-1,0, 0,0,0,1),
            VirtualResolution = 32, PageSize = 8, AtlasSize = 16, PhysicalPagesPerRow = 2,
            MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1, ClipmapLevelCount = 1,
            ClipmapLevel = 6, FirstClipmapLevel = 6, WorldUnitsPerCentimeter = .01f,
            LightDirection = new(0,0,-1),
        };
        uint[] pages = requests.Execute(view, """
            Texture2DArray<float> ReceiverDepth;
            [shader("compute")]
            [numthreads(8,8,1)]
            void RequestDepthSamples(uint3 pixel : SV_DispatchThreadID)
            {
                float depth = ReceiverDepth.Load(int4(pixel,0));
                if (depth == 1.0) return;
                float2 position = (float2(pixel.xy) + .5) / 16.0 * float2(1,-1) + float2(-.5,.5);
                MarkVirtualShadowPages(float3(position,-depth), pixel.z);
            }
            """, "RequestDepthSamples", new(2,2,1),
            new SampledImage(16,16,depth,0,Format.R32Float,TextureViewDimension.Texture2DArray,Name:"ReceiverDepth"));
        Assert.Equal(new uint[] { 1,1,1,1 }, requests.ReadCoverage(view,pages,
            [new(-.4375f,.4375f,-.5f),new(-.0625f,.4375f,-.5f),
             new(-.4375f,.0625f,-.5f),new(-.0625f,.0625f,-.5f)]));
    }
}
