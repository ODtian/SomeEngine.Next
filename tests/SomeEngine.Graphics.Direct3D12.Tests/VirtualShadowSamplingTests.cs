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
        view[34] = 7;
        view[35] = 16384;
        view[36] = lightKind;
        uint[] pages = new uint[(16384 + 64) * 4];
        pages[16384 * 4] = 4;
        pages[16384 * 4 + 3] = 7;
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
                uint page;
                bool found = TryResolveVirtualShadowCoarsePage(view, Pages,
                    VirtualShadowCoarseWorldPage(view, pixel), page);
                Result[id.x] = uint4(found ? 1u : 0u, page, VirtualShadowCoarseAtlasPixel(view, pixel));
            }
            """, "CheckCorners", new(1, 1, 1),
            [(view, ResourceBindingType.BufferSrv, 168), (pages, ResourceBindingType.BufferSrv, 16),
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
    public void DirectionalSamplingUsesNearestResidentClipmap(int firstResident)
    {
        uint[] views = Enumerable.Range(0, 3).SelectMany(CreateView).ToArray();
        uint[] pages = new uint[3 * 256 * 4];
        for (int level = 0; level < 3; level++)
        {
            int entry = (level * 256 + 6 * 16 + 10) * 4;
            pages[entry] = (uint)level + 1;
            pages[entry + 1] = 10;
            pages[entry + 2] = 6;
            pages[entry + 3] = level < firstResident ? 6u : 7u;
        }
        uint[][] result = ExecuteSource("""
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void FindView()
            {
                uint view;
                float3 ndc;
                float2 uv;
                bool found = TryFindDirectionalVirtualShadowView(
                    Views, Pages, 0u, 3u, float3(0.25, 0.25, 0.5), view, ndc, uv);
                Result[0] = found ? view : 0xFFFFFFFFu;
            }
            """, "FindView", new(1, 1, 1),
            [(views, ResourceBindingType.BufferSrv, 168), (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal((uint)firstResident, result[0][0]);
    }

    [Fact]
    public void DirectionalCoarseRequestsFollowTheProjectedCamera()
    {
        uint[] view = CreateView(0);
        view[28] = BitConverter.SingleToUInt32Bits(0.0625f);
        view[30] = BitConverter.SingleToUInt32Bits(0.5f);
        uint[][] result = Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
            [([1, 512, 9, 0], ResourceBindingType.ConstantBuffer, 0),
             (view, ResourceBindingType.BufferSrv, 168), (new uint[512], ResourceBindingType.BufferUav, 4)]);
        Assert.Equal(new[] { 120, 136 }, Enumerable.Range(0, 512).Where(index => result[0][index] == 9));
    }

    private static uint[] CreateView(int level)
    {
        uint[] view = new uint[42];
        Matrix4x4 matrix = Matrix4x4.Identity;
        MemoryMarshal.Write(MemoryMarshal.AsBytes(view.AsSpan()), in matrix);
        view[16] = 2048;
        view[17] = 128;
        view[23] = (uint)level * 256;
        view[24] = 7;
        view[26] = (uint)level;
        view[33] = 3;
        view[34] = 1;
        view[35] = 256;
        return view;
    }
}
