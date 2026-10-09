using Xunit;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowResidentPagesTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void UnfinishedFineDepthUsesTheCompletedResidentAncestor()
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = System.Numerics.Matrix4x4.Identity,
            VirtualResolution = 32, PageSize = 8, AtlasSize = 16, PhysicalPagesPerRow = 2,
            MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 3, LightKind = VirtualShadowLightKind.Spot,
        }];
        uint[] view = MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray();
        uint[] pages = new uint[21];
        pages[0] = VirtualShadowPageEntry.Encode(1, 2) & ~0x80000000u;
        pages[20] = VirtualShadowPageEntry.Encode(0, 2);
        uint[][] result = new D3D12TestCompute(compiler).Execute("vsm_resident_propagation.slang",
            "PropagateVirtualShadowResidentPages", new(1,1,1),
            [new BufferInput("ResidentUpdate", [1,21,21,0], ResourceBindingType.ConstantBuffer, 0),
             new BufferInput("ResidentShadowViews", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("CompletedPageTable", pages, ResourceBindingType.BufferUav, 4)]);
        uint[][] sample = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            StructuredBuffer<uint> Residents;
            Texture2DArray<uint> Atlas;
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
            [(view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes), (result[0], ResourceBindingType.BufferSrv, 4),
             (result[0], ResourceBindingType.BufferSrv, 4), (new uint[2], ResourceBindingType.BufferUav, 8)],
            new SampledImage(16, 16, Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.6f), 256).ToArray(), 3, ViewDimension: TextureViewDimension.Texture2DArray));
        Assert.Equal(1f, BitConverter.UInt32BitsToSingle(sample[0][0]));
        Assert.InRange(BitConverter.UInt32BitsToSingle(sample[0][1]), .3999999f, .4000001f);
    }
}
