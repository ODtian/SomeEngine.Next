using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowResidentPagesTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void UnfinishedFineDepthUsesTheCompletedResidentAncestor()
    {
        uint[] view = new uint[68];
        view[16] = 32; view[17] = 8; view[18] = 16; view[19] = 2;
        view[20] = 4; view[24] = 7; view[34] = 3; view[37] = 1;
        uint[] pages = new uint[21 * 4];
        pages[0] = 2;
        pages[20 * 4] = 1; pages[20 * 4 + 3] = 7;
        uint[][] result = new D3D12TestCompute(compiler).Execute("vsm_resident_propagation.slang",
            "PropagateVirtualShadowResidentPages", new(1,1,1),
            [new BufferInput("ResidentUpdate", [1,21,21,0], ResourceBindingType.ConstantBuffer, 0),
             new BufferInput("ResidentShadowViews", view, ResourceBindingType.BufferSrv, 272),
             new BufferInput("CompletedPageTable", pages, ResourceBindingType.BufferSrv, 16),
             new BufferInput("ResidentPages", new uint[42], ResourceBindingType.BufferUav, 8)]);
        uint[][] sample = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            StructuredBuffer<uint2> Residents;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float2> Sample;
            [shader("compute")]
            [numthreads(1,1,1)]
            void ReadResidentDepth()
            {
                float depth;
                uint mip;
                bool valid = TryReadVirtualShadowResidentDepth(Views[0], Pages, Residents, Atlas,
                    0u, float2(.1,.1), depth, mip);
                Sample[0] = float2(valid ? 1.0 : 0.0, depth);
            }
            """, "ReadResidentDepth", new(1,1,1),
            [(view, ResourceBindingType.BufferSrv, 272), (pages, ResourceBindingType.BufferSrv, 16),
             (result[0], ResourceBindingType.BufferSrv, 8), (new uint[2], ResourceBindingType.BufferUav, 8)],
            new SampledImage(16, 16, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.6f), 256).ToArray(), 3));
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(sample[0][0]));
        Assert.InRange(BitConverter.UInt32BitsToSingle(sample[0][1]), .3999999f, .4000001f);
    }
}
