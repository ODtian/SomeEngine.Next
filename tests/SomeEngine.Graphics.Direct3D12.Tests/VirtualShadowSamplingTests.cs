using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class VirtualShadowSamplingTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(2, 1, 0)]
    [InlineData(2, 2, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(4, 0, 1)]
    [InlineData(2, 0, 2)]
    public void ReceiverDepthsPreserveShadowCoverageAtViewportAndDepthEdges(int pixelIndex, int depthEdge, int planeAxis)
    {
        const int levels = 4, pageEntries = 16;
        uint[] views = new uint[levels * ViewWords];
        uint[] pages = new uint[levels * pageEntries];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < levels; level++)
        {
            uint[] view = CreateView(level);
            Matrix4x4 rotation = planeAxis switch
            {
                0 => Matrix4x4.CreateRotationY(MathF.PI * .5f),
                1 => Matrix4x4.CreateRotationX(MathF.PI * .5f),
                _ => Matrix4x4.Identity,
            };
            Matrix4x4 projection = rotation *
                Matrix4x4.CreateScale(1.0f / (4 << level), 1.0f / (4 << level), 1) *
                Matrix4x4.CreateTranslation(planeAxis == 0 ? -.5f / (4 << level) : 0,
                    planeAxis == 1 ? .5f / (4 << level) : 0, planeAxis == 2 ? 0 : .5f);
            MemoryMarshal.Cast<uint, VirtualShadowView>(view.AsSpan())[0].LightViewProjection = projection;
            view[16] = 512;
            view[23] = (uint)(level * pageEntries);
            view[30] = BitConverter.SingleToUInt32Bits(-5);
            view.CopyTo(views, level * ViewWords);
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                int entry = level * pageEntries + row * 4 + column;
                pages[entry] = VirtualShadowPageEntry.Encode((uint)level, 4);
            }
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(level == 2 ? .75f : .25f),
                    row * 512 + level * 128, 128);
        }
        // The receivers are z = x + 0.5, z = -y + 0.5, and z = 0.5.
        // Their camera distance selects the second level across all receiver edges.
        uint[] depths = Enumerable.Range(0, 25)
            .Select(index => BitConverter.SingleToUInt32Bits(planeAxis == 2 ? .5f :
                .3f + .1f * (planeAxis == 0 ? index % 5 : index / 5))).ToArray();
        if (depthEdge != 0) depths[13] = BitConverter.SingleToUInt32Bits(depthEdge == 1 ? 1.0f : .95f);
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            #include "vsm_receiver_pixel.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            Texture2DArray<float> ReceiverDepth;
            StructuredBuffer<uint> Pixel;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SampleReceiver()
            {
                uint3 pixel = uint3(Pixel[0], Pixel[1], 0u);
                float4x4 clipToWorld = float4x4(.25,0,0,0, 0,.25,0,0, 0,0,1,0, 0,0,0,1);
                VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
                    ReceiverDepth, pixel, uint2(5u, 5u), ReceiverDepth.Load(int4(pixel, 0)), clipToWorld);
                float depth = ReceiverDepth.Load(int4(pixel, 0));
                float depthDx = VirtualShadowReceiverDepthStep(ReceiverDepth, pixel, uint2(5u, 5u), depth, uint2(1u, 0u));
                float depthDy = VirtualShadowReceiverDepthStep(ReceiverDepth, pixel, uint2(5u, 5u), depth, uint2(0u, 1u));
                float3 normal = cross(receiver.PositionDx - receiver.Position + float3(0.0, 0.0, depthDx),
                    receiver.PositionDy - receiver.Position + float3(0.0, 0.0, depthDy));
                Result[0] = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 4u, 0.0,
                    receiver.Position, receiver.PositionDx, receiver.PositionDy, 1u, 1u, 0.0, 0.0, normal);
            }
            """, "SampleReceiver", new(1, 1, 1),
            [new BufferInput("Views", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("Pixel", [(uint)(planeAxis == 1 ? 2 : pixelIndex), (uint)(planeAxis == 1 ? pixelIndex : 2)],
                 ResourceBindingType.BufferSrv, 4),
             new BufferInput("Result", new uint[1], ResourceBindingType.BufferUav, 4)],
            images: [new(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray, Name: "Atlas"),
                new(5, 5, depths, 3, Format.R32Float, TextureViewDimension.Texture2DArray, Name: "ReceiverDepth")]);
        Assert.Equal(0.0f, BitConverter.UInt32BitsToSingle(result[0][0]));
    }

    [Theory]
    [InlineData(0, 0.0f, 0.0f, 1.0f, 0.0f, 0)]
    [InlineData(1, 0.0f, 0.0f, 1.0f, 0.0f, 1)]
    [InlineData(2, 0.0f, 0.0f, 1.0f, 0.0f, 2)]
    [InlineData(1, 1.0f, 0.0f, 1.0f, 0.0f, 2)]
    [InlineData(1, 0.0f, 10000.0f, 1.0f, 0.0f, 1)]
    [InlineData(0, 0.0f, 0.0f, 8.0f, 0.0f, 0)]
    [InlineData(1, 0.0f, 0.0f, 8.0f, 0.0f, 1)]
    [InlineData(3, 0.0f, 0.0f, 1.0f, 8.0f, 3)]
    public void DirectionalShadowsFollowReceiverDistanceAndQualityBias(
        int distanceLevel, float qualityBias, float cameraOrigin, float stretch, float shear, int selectedLevel)
    {
        const int levels = 4, resolution = 512, pageEntries = 16;
        uint[] views = new uint[levels * ViewWords];
        uint[] pages = new uint[levels * pageEntries];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < levels; level++)
        {
            uint[] view = CreateView(level);
            Matrix4x4 projection = Matrix4x4.CreateTranslation(-cameraOrigin, 0, 0)
                * Matrix4x4.CreateScale(1.0f / (4 << level), 1.0f / (4 << level), 1);
            MemoryMarshal.Cast<uint, VirtualShadowView>(view.AsSpan())[0].LightViewProjection = projection;
            view[16] = resolution;
            view[23] = (uint)(level * pageEntries);
            view[28] = BitConverter.SingleToUInt32Bits(cameraOrigin);
            view[30] = BitConverter.SingleToUInt32Bits(.5f - 1.5f * (1 << distanceLevel));
            view[31] = BitConverter.SingleToUInt32Bits(qualityBias);
            view.CopyTo(views, level * ViewWords);
            int entry = level * pageEntries + 10;
            pages[entry] = VirtualShadowPageEntry.Encode((uint)level, 4);
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(level == selectedLevel ? .75f : .25f),
                    y * 512 + level * 128, 128);
        }
        float step = 1.5f * (1 << distanceLevel) / 64;
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float3> PixelOffsets;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SamplePixelFootprint()
            {
                float3 world = PixelOffsets[2];
                Result[0] = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 4u, 0.0,
                    world, world + PixelOffsets[0], world + PixelOffsets[1],
                    1u, 1u, 0.0, 0.0);
            }
            """, "SamplePixelFootprint", new(1, 1, 1),
            [new BufferInput("Views", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("PixelOffsets", [BitConverter.SingleToUInt32Bits(step), 0u, 0u,
                 BitConverter.SingleToUInt32Bits(step * shear), BitConverter.SingleToUInt32Bits(step * stretch), 0u,
                 BitConverter.SingleToUInt32Bits(cameraOrigin), 0u, BitConverter.SingleToUInt32Bits(.5f)], ResourceBindingType.BufferSrv, 12),
             new BufferInput("Result", new uint[1], ResourceBindingType.BufferUav, 4)],
            new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray, Name: "Atlas"));
        Assert.Equal(0.0f, BitConverter.UInt32BitsToSingle(result[0][0]));
    }

    [Theory]
    [InlineData(0, 1.0f, 0.0f, 0)]
    [InlineData(1, 1.0f, 0.0f, 1)]
    [InlineData(1, 8.0f, 0.0f, 1)]
    [InlineData(3, 1.0f, 8.0f, 0)]
    public void SpotShadowsMatchTheReceiverPixelFootprint(int pixelMip, float stretch, float shear, int selectedMip)
    {
        uint[] view = CreateView(0);
        view[16] = 1024;
        view[34] = 4;
        view[37] = 2;
        uint[] pages = new uint[85];
        uint[] pixels = new uint[512 * 128];
        int tableOffset = 0;
        for (int mip = 0; mip < 4; mip++)
        {
            int dimension = 8 >> mip;
            int center = dimension / 2;
            int entry = tableOffset + center * dimension + center;
            pages[entry] = VirtualShadowPageEntry.Encode((uint)mip, 4);
            tableOffset += dimension * dimension;
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(mip == selectedMip ? .75f : .25f),
                    y * 512 + mip * 128, 128);
        }
        float step = 1.5f * (1 << pixelMip) / 512;
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float3> PixelOffsets;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SamplePixelFootprint()
            {
                float3 world = float3(0.0, 0.0, 0.5);
                float radius = .25 * GetVirtualShadowPixelWidth(PixelOffsets[0].xy, PixelOffsets[1].xy);
                Result[0] = SampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, radius,
                    world, world + PixelOffsets[0], world + PixelOffsets[1], 1u, 1u, 0.0, 0.0);
            }
            """, "SamplePixelFootprint", new(1, 1, 1),
            [new BufferInput("Views", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("PixelOffsets", [BitConverter.SingleToUInt32Bits(step), 0u, 0u,
                 BitConverter.SingleToUInt32Bits(step * shear), BitConverter.SingleToUInt32Bits(step * stretch), 0u], ResourceBindingType.BufferSrv, 12),
             new BufferInput("Result", new uint[1], ResourceBindingType.BufferUav, 4)],
            new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray, Name: "Atlas"));
        Assert.Equal(0.0f, BitConverter.UInt32BitsToSingle(result[0][0]));
    }

    [Theory]
    [InlineData(64.0f, 64.0f, true, false, .2f)]
    [InlineData(127.75f, 64.0f, true, true, .2f)]
    [InlineData(127.75f, 127.75f, true, true, .2f)]
    [InlineData(200.0f, 200.0f, false, false, .2f)]
    [InlineData(125.5f, 64.0f, true, false, .49f)]
    public void FilteredShadowsPreservePageInteriorsBordersAndCoarseFallback(
        float pixelX, float pixelY, bool finePages, bool mixed, float occluderDepth)
    {
        uint[] view = CreateView(0);
        view[16] = 256;
        view[34] = 2;
        uint[] pages = new uint[5];
        for (int page = 0; page < 5; page++)
        {
            if (page < 4 && !finePages) continue;
            pages[page] = VirtualShadowPageEntry.Encode((uint)page, 4);
        }
        uint[] pixels = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.2f), 512 * 256).ToArray();
        for (int page = 0; page <= 4; page += 4)
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(1.0f - occluderDepth), (page / 4 * 128 + y) * 512, 128);
        uint[] position = [BitConverter.SingleToUInt32Bits(pixelX), BitConverter.SingleToUInt32Bits(pixelY)];
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float2> Pixels;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SampleShadow()
            {
                float2 uv = Pixels[0] / 256.0;
                float3 world = float3(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.5);
                Result[0] = SampleVirtualShadowMapFiltered(Views[0], Pages, Atlas, world,
                    0u, 8u, 16u, 4.0, 8.0);
            }
            """, "SampleShadow", new(1, 1, 1),
            [new BufferInput("Views", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("Pixels", position, ResourceBindingType.BufferSrv, 8),
             new BufferInput("Result", new uint[1], ResourceBindingType.BufferUav, 4)],
            new SampledImage(512, 256, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray, Name: "Atlas"));
        float visibility = BitConverter.UInt32BitsToSingle(result[0][0]);
        if (mixed) Assert.InRange(visibility, 1.0f / 16, 15.0f / 16);
        else Assert.Equal(0.0f, visibility);
    }

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
        uint[] pages = new uint[lastMipOffset + 1];
        pages[lastMipOffset] = VirtualShadowPageEntry.Encode(3, 4);
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
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
            [new BufferInput("Views", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("Result", new uint[16], ResourceBindingType.BufferUav, 16)]);
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
        uint[] pages = new uint[3 * 256];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < 3; level++)
        {
            int entry = level * 256 + 6 * 16 + 10;
            pages[entry] = level < firstResident ? 0 : VirtualShadowPageEntry.Encode((uint)level, 4);
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(level == firstResident ? .75f : .25f), y * 512 + level * 128, 128);
        }
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void FindView()
            {
                float visibility = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 3u, 0.0,
                    float3(0.25, 0.25, 0.5), float3(0.2501, 0.25, 0.5),
                    float3(0.25, 0.2501, 0.5), 1u, 1u, 0.0, 0.0);
                Result[0] = visibility > 0.5 ? 1u : 0u;
            }
            """, "FindView", new(1, 1, 1),
            [new BufferInput("Views", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("Pages", pages, ResourceBindingType.BufferSrv, 4),
             new BufferInput("Result", new uint[1], ResourceBindingType.BufferUav, 4)],
            new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray, Name: "Atlas"));
        Assert.Equal(firstResident == 3 ? 1u : 0u, result[0][0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalCoarseRequestsCoverTheSelectedCenter(bool selectedLevel)
    {
        uint[] view = CreateView(2);
        view[23] = 0;
        view[34] = 5;
        view[36] = selectedLevel ? 1u : 0u;
        const int pages = 341;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
            [new BufferInput("CoarsePageDemand", [1, pages, 9, 0], ResourceBindingType.ConstantBuffer, 0),
             new BufferInput("ShadowViews", view, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new BufferInput("RequestedPageFlags", new uint[pages], ResourceBindingType.BufferUav, 4),
             new BufferInput("RequestedPageIndices", new uint[pages], ResourceBindingType.BufferUav, 4),
             new BufferInput("RequestedPageCount", [0], ResourceBindingType.BufferUav, 4),
             new BufferInput("PageReceiverMasks", null, ResourceBindingType.TextureUav, 0)]);
        int[] expected = selectedLevel ? [119, 120, 135, 136] : [];
        Assert.Equal(expected, Enumerable.Range(0, pages).Where(index => (result[0][index] & 2) != 0));
        Assert.Equal((uint)expected.Length, result[2][0]);
    }

    private static uint[] CreateView(int level)
    {
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity,
            VirtualResolution = 2048, PageSize = 128, AtlasSize = 512, PhysicalPagesPerRow = 4,
            MaxPhysicalPages = 16, PageTableOffset = (uint)level * 256, CacheGeneration = 7,
            ClipmapLevel = level, ClipmapLevelCount = 3, MipLevelCount = 1, WorldUnitsPerCentimeter = 1,
        };
        return MemoryMarshal.Cast<VirtualShadowView, uint>(MemoryMarshal.CreateReadOnlySpan(ref view, 1)).ToArray();
    }

    private const int ViewWords = VirtualShadowView.SizeInBytes / sizeof(uint);
}
