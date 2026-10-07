using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class RasterSubpixelCoverageTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void SoftwareRasterCoversThePixelCenterWithinTheRoundedTriangle()
    {
        uint[][] pixels = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "visbuffer_encoding.slang"
            #include "cluster_raster_shared.slang"
            RWStructuredBuffer<uint> Pixels;
            struct PixelOutput : IClusterRasterOutput
            {
                int4 scissor(uint visible, uint policy, uint target, uint width, uint height)
                { return int4(0,0,int(width),int(height)); }
                void write(int2 pixel, uint view, uint policy, uint target, float depth, uint visibility)
                { Pixels[pixel.y * 2 + pixel.x] = 1u; }
            };
            [shader("compute")]
            [numthreads(1,1,1)]
            void RasterizeTriangle()
            {
                float4 clip[3] = { float4(-.4994,1,.5,1), float4(-.4994,-1,.5,1), float4(1,1,.5,1) };
                ClusterPixelRasterTask<float,OpaqueVisibilityEvaluate<float>,PixelOutput> task =
                    (ClusterPixelRasterTask<float,OpaqueVisibilityEvaluate<float>,PixelOutput>)0;
                task.Triangle = SetupClusterRasterTriangle(clip,int4(0,0,2,2),2u,2u);
                task.SourceBarycentric0 = float3(1,0,0);
                task.SourceBarycentric1 = float3(0,1,0);
                task.SourceBarycentric2 = float3(0,0,1);
                OpaqueVisibilityEvaluate<float> visibility;
                uint count = uint((task.Triangle.MaxPixel.x - task.Triangle.MinPixel.x + 1) *
                    (task.Triangle.MaxPixel.y - task.Triangle.MinPixel.y + 1));
                for (uint index=0u; index<count; index++) task.RasterizePixels(visibility,index);
            }
            """, "RasterizeTriangle", new(1,1,1),
            [(new uint[4],ResourceBindingType.BufferSrv,0), (new uint[1],ResourceBindingType.BufferSrv,4),
             (new uint[4],ResourceBindingType.BufferUav,4)]);
        Assert.Equal(1u, pixels[0][0]);
    }
}
