using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class RasterDepthClipTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void LocalDepthClippingRejectsTheTriangleBeforeTheNearPlaneWithDifferentClipWValues()
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
            [numthreads(32,1,1)]
            void RasterizeLocalTriangle(uint lane : SV_GroupIndex)
            {
                float4 clip[3] = {float4(-1,1,-.25,1), float4(-2,-2,-.5,2), float4(4,4,-1,4)};
                EmptyVisibilityVarying varying[3] = { {}, {}, {} };
                OpaqueVisibilityEvaluate<float> visibility;
                PixelOutput output;
                RasterizeClusterTriangleWave<float,OpaqueVisibilityEvaluate<float>,PixelOutput>(
                    visibility,output,clip,varying,int4(0,0,2,2),0u,0u,0u,1u,0u,lane==0u,2u,2u,true);
            }
            """, "RasterizeLocalTriangle", new(1,1,1),
            [(new uint[4],ResourceBindingType.BufferSrv,0), (new uint[1],ResourceBindingType.BufferSrv,4),
             (new uint[4],ResourceBindingType.BufferUav,4)]);
        Assert.Equal(new uint[] {0,0,0,0},pixels[0]);
    }
}
