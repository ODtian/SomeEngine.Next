using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowSamplingTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void LocalLightFallbackCoversAllProjectionCorners(uint lightKind)
    {
        uint[] view = CreateView(0);
        view[16] = 16384;
        view[34] = 8;
        view[37] = lightKind;
        const int lastMipOffset = 21844;
        uint[] pages = new uint[(lastMipOffset + 1) * 4];
        pages[lastMipOffset * 4] = 4;
        pages[lastMipOffset * 4 + 3] = 7;
        uint[][] result = ExecuteSource("""
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            RWStructuredBuffer<uint4> Result;
            [shader("compute")]
            [numthreads(4, 1, 1)]
            void CheckCorners(uint3 id : SV_DispatchThreadID)
            {
                VirtualShadowMapParameters view = Views[0];
                uint2 pixel = uint2(id.x & 1u, id.x >> 1u) * (view.VirtualResolution - 1u);
                float2 uv = (float2(pixel) + 0.5) / float(view.VirtualResolution);
                VirtualShadowFilterContext context = BuildVirtualShadowFilterContext(view, Pages, 0u, uv);
                Result[id.x] = uint4(context.Resident ? 1u : 0u, context.AtlasPage,
                    VirtualShadowMipPagePixel(view, context.MipLevel, pixel));
            }
            """, "CheckCorners", new(1, 1, 1),
            [(view, ResourceBindingType.BufferSrv, 172), (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[16], ResourceBindingType.BufferUav, 16)]);
        for (int corner = 0; corner < 4; corner++)
        {
            Assert.Equal(1u, result[0][corner * 4]);
            Assert.Equal(3u, result[0][corner * 4 + 1]);
            Assert.Equal((uint)(corner & 1) * 127, result[0][corner * 4 + 2]);
            Assert.Equal((uint)(corner >> 1) * 127, result[0][corner * 4 + 3]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DirectionalSamplingUsesNearestResidentClipmap(int firstResident)
    {
        uint[] views = Enumerable.Range(0, 3).SelectMany(CreateView).ToArray();
        uint[] pages = new uint[3 * 256 * 4];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < 3; level++)
        {
            int entry = (level * 256 + 6 * 16 + 10) * 4;
            pages[entry] = (uint)level + 1;
            pages[entry + 1] = 10;
            pages[entry + 2] = 6;
            pages[entry + 3] = level < firstResident ? 6u : 7u;
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(level == firstResident ? .75f : .25f), y * 512 + level * 128, 128);
        }
        uint[][] result = ExecuteSource("""
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void FindView()
            {
                float visibility = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 3u, 0.0,
                    float3(0.25, 0.25, 0.5), 1u, 1u, 0.0, 0.0);
                Result[0] = visibility > 0.5 ? 1u : 0u;
            }
            """, "FindView", new(1, 1, 1),
            [(views, ResourceBindingType.BufferSrv, 172), (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2));
        Assert.Equal(firstResident == 3 ? 1u : 0u, result[0][0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalCoarseRequestsCoverSelectedCenterAndLastMip(bool selectedLevel)
    {
        uint[] view = CreateView(2);
        view[23] = 0;
        view[34] = 5;
        view[36] = selectedLevel ? 1u : 0u;
        const int pages = 341;
        uint[][] result = Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
            [([1, pages, 9, 0], ResourceBindingType.ConstantBuffer, 0),
             (view, ResourceBindingType.BufferSrv, 172), (new uint[pages], ResourceBindingType.BufferUav, 4),
             (new uint[pages], ResourceBindingType.BufferUav, 4), (new uint[pages], ResourceBindingType.BufferUav, 4),
             ([0], ResourceBindingType.BufferUav, 4), (new uint[pages * 2], ResourceBindingType.BufferUav, 8)]);
        int[] expected = selectedLevel ? [119, 120, 135, 136, 340] : [340];
        Assert.Equal(expected, Enumerable.Range(0, pages).Where(index => result[0][index] == 9));
        Assert.Equal((uint)expected.Length, result[3][0]);
    }

    private static uint[] CreateView(int level)
    {
        uint[] view = new uint[43];
        Matrix4x4 matrix = Matrix4x4.Identity;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(view.AsSpan()), in matrix);
        view[16] = 2048;
        view[17] = 128;
        view[18] = 512;
        view[19] = 4;
        view[23] = (uint)level * 256;
        view[24] = 7;
        view[26] = (uint)level;
        view[33] = 3;
        view[34] = 1;
        return view;
    }
}
