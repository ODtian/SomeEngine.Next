using System.Numerics;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowReceiverLayersTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void UserDepthSamplesRequestCurrentAndReprojectedReceiverCoverage()
    {
        var requests = new ShadowReceiverRequests(compiler);
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.CreateTranslation(0,0,-.6f),
            VirtualResolution = 32, PageSize = 8, AtlasSize = 16,
            PhysicalPagesPerRow = 2, MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1,
            ClipmapLevelCount = 1, ClipmapLevel = 6, FirstClipmapLevel = 6,
            WorldUnitsPerCentimeter = .01f, LightDirection = new(0,0,-1),
        };
        uint[] pages = requests.Execute(view, """
            Texture2DArray<float> PreviousDepth;
            struct LayerMaterial
            {
                void markShadowPages(uint3 pixel)
                {
                    float3 position = float3(-.75 + .5 * float(pixel.x),0,.8);
                    if (pixel.x == 0u || pixel.x == 2u)
                        MarkVirtualShadowPages(position, pixel.z);
                    if (pixel.x == 1u)
                    {
                        float4x4 currentToPrevious = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, .5,0,0,1);
                        float4 previousClip = mul(float4(position,1),currentToPrevious);
                        float2 previousUV = previousClip.xy / previousClip.w * float2(.5,-.5) + .5;
                        uint2 previousPixel = uint2(previousUV * float2(4,1));
                        float depth = PreviousDepth.Load(int4(previousPixel,pixel.z,0));
                        float4 previousPosition = float4(
                            (float2(previousPixel) + .5) / float2(4,1) * float2(2,-2) + float2(-1,1),depth,1);
                        float4x4 previousClipToWorld = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, -.5,0,0,1);
                        float4 world = mul(previousPosition,previousClipToWorld);
                        MarkVirtualShadowPages(world.xyz / world.w, pixel.z);
                    }
                }
            };
            [shader("compute")]
            [numthreads(4,1,1)]
            void RequestUserDepths(uint3 pixel : SV_DispatchThreadID)
            {
                LayerMaterial material;
                material.markShadowPages(pixel);
            }
            """, "RequestUserDepths", new(1,1,1),
            new SampledImage(4,1,new[] { .2f,.2f,.8f,.2f }.Select(BitConverter.SingleToUInt32Bits).ToArray(),0,
                Format.R32Float,TextureViewDimension.Texture2DArray,Name:"PreviousDepth"));
        Assert.Equal(new uint[] { 1,1,1,0 }, requests.ReadCoverage(view,pages,
            [new(-.75f,0,.8f),new(-.25f,0,.8f),new(.25f,0,.8f),new(.75f,0,.8f)]));
    }
}
