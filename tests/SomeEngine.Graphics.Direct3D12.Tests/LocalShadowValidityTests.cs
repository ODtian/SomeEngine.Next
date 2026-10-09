using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class LocalShadowValidityTests(D3D12TestCompiler compiler)
{
    [Theory]
    [InlineData("ObservePoint", 6)]
    [InlineData("ObserveSpot", 1)]
    public void PublicTrySamplingDistinguishesMissingUnfinishedFineCoarseAndReceiverCoverage(string entry, int faces)
    {
        // Authored public shader inputs; assertions observe Try's validity and visibility.
        const int queries = 9, entries = 5, pageSize = 8, atlasSize = 32;
        var views = new VirtualShadowView[queries * faces];
        uint[] pages = new uint[queries * entries], masks = new uint[queries * entries * 2];
        uint[] atlas = new uint[atlasSize * atlasSize];
        for (int query = 0; query < queries; query++)
        {
            for (int face = 0; face < faces; face++)
                views[query * faces + face] = new VirtualShadowView { LightViewProjection = Matrix4x4.Identity,
                    VirtualResolution = 16, PageSize = pageSize, AtlasSize = atlasSize, PhysicalPagesPerRow = 4,
                    MaxPhysicalPages = 16, PageTableOffset = (uint)(query * entries), CacheGeneration = 7,
                    MipLevelCount = 2, LightKind = faces == 6 ? VirtualShadowLightKind.Point : VirtualShadowLightKind.Spot,
                    FaceIndex = (uint)face, LightRange = 10 };
            // Fine sample is page (1,0), coarse is (0,0). Atlas depth is reversed.
            uint fine = (uint)Math.Min(query * 2, 14), coarse = fine + 1;
            FillDepth(fine, query == 4 ? 1f : .25f);
            FillDepth(coarse, .25f);
            if (query == 1 || query == 3) Put(query, 1, fine, 1, 0, 0); // mapped but unfinished
            if (query == 4 || query == 5 || query == 8) Put(query, 1, fine, 1, 0, 7);
            if (query == 2 || query == 3 || query == 6) Put(query, 4, coarse, 0, 0, 7);
            if (query == 6 || query == 7)
            {
                Put(query, 1, fine, 1, 0, 7u | 0x80000000u);
                if (query == 7) masks[(query * entries + 1) * 2 + 1] = 1u << 18; // actual sample cell (2,6)
            }
        }
        uint[] viewWords = MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray();
        uint[][] result = new D3D12TestCompute(compiler).ExecuteSource(Shader, entry, new(1, 1, 1),
            [(masks, ResourceBindingType.BufferSrv, 8), (viewWords, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes),
             (pages, ResourceBindingType.BufferSrv, 4), (new uint[queries * 2], ResourceBindingType.BufferUav, 8)],
            new SampledImage(atlasSize, atlasSize, atlas, 3, ViewDimension: TextureViewDimension.Texture2DArray));
        float[] observations = result[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.Equal(new float[] { 0,1, 0,1, 1,0, 1,0, 1,1, 1,0, 1,0, 1,0, 0,1 }, observations);

        void Put(int query, int page, uint physical, uint x, uint y, uint generation)
        {
            int offset = query * entries + page;
            pages[offset] = VirtualShadowPageEntry.Encode(physical, 4u);
            if ((generation & 0x7fffffffu) != 7u) pages[offset] &= ~0x80000000u;
            if ((generation & 0x80000000u) != 0u) pages[offset] |= 0x04000000u;
        }
        void FillDepth(uint physical, float depth)
        {
            int originX = (int)(physical % 4) * pageSize, originY = (int)(physical / 4) * pageSize;
            for (int y = 0; y < pageSize; y++)
            for (int x = 0; x < pageSize; x++) atlas[(originY + y) * atlasSize + originX + x] = BitConverter.SingleToUInt32Bits(1 - depth);
        }
    }

    private const string Shader = """
        #include "vsm_sampling.slang"
        StructuredBuffer<VirtualShadowMapParameters> Views;
        StructuredBuffer<uint> Pages;
        Texture2DArray<uint> Atlas;
        RWStructuredBuffer<float2> Observations;
        [shader("compute")]
        [numthreads(16,1,1)]
        void ObservePoint(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= 9u) return;
            float3 position = float3(.25,.25,.5);
            float visibility;
            bool valid = TrySamplePointVirtualShadowMap(Views, Pages, Atlas, id.x * 6u, 0.0, 0.0,
                position, position + float3(.001,0,0), position + float3(0,.001,0),
                1u,16u,0.0,id.x == 8u ? 4.0 : 0.0,float3(0,0,1),visibility);
            Observations[id.x] = float2(valid ? 1.0 : 0.0, visibility);
        }
        [shader("compute")]
        [numthreads(16,1,1)]
        void ObserveSpot(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= 9u) return;
            float3 position = float3(.25,.25,.5);
            float visibility;
            bool valid = TrySampleSpotVirtualShadowMap(Views, Pages, Atlas, id.x, 0.0, 0.0,
                position, position + float3(.001,0,0), position + float3(0,.001,0),
                1u,16u,0.0,id.x == 8u ? 4.0 : 0.0,float3(0,0,1),visibility);
            Observations[id.x] = float2(valid ? 1.0 : 0.0, visibility);
        }
        """;
}
