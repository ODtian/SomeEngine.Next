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
    public void ReceiverPositionsPreserveCameraPixelsAtViewportAndDepthEdges(int pixelIndex, int depthEdge, int planeAxis)
    {
        // Authored planes z=x+.5, z=-y+.5 and z=.5 have world pixel spacing .1.
        // Nominal camera neighbors retain the receiver's camera depth.
        uint[] depths = Enumerable.Range(0, 25)
            .Select(index => BitConverter.SingleToUInt32Bits(planeAxis == 2 ? .5f :
                .3f + .1f * (planeAxis == 0 ? index % 5 : index / 5))).ToArray();
        if (depthEdge != 0) depths[13] = BitConverter.SingleToUInt32Bits(depthEdge == 1 ? 1f : .95f);
        int pixelX = planeAxis == 1 ? 2 : pixelIndex;
        int pixelY = planeAxis == 1 ? pixelIndex : 2;
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #include "vsm_receiver_pixel.slang"
            Texture2DArray<float> ReceiverDepth;
            StructuredBuffer<uint> Pixel;
            RWStructuredBuffer<float3> Positions;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadReceiverPlane()
            {
                uint3 pixel = uint3(Pixel[0], Pixel[1], 0u);
                float4x4 clipToWorld = float4x4(.25,0,0,0, 0,.25,0,0, 0,0,1,0, 0,0,0,1);
                VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
                    ReceiverDepth, pixel, uint2(5u, 5u), ReceiverDepth.Load(int4(pixel, 0)), clipToWorld);
                Positions[0] = receiver.Position;
                Positions[1] = receiver.PositionDx;
                Positions[2] = receiver.PositionDy;
            }
            """, "ReadReceiverPlane", new(1, 1, 1),
            [([(uint)pixelX, (uint)pixelY], ResourceBindingType.BufferSrv, 4),
             (new uint[9], ResourceBindingType.BufferUav, 12)],
            images: [new(5, 5, depths, 0, Format.R32Float, TextureViewDimension.Texture2DArray)]);
        float x = -.2f + .1f * pixelX;
        float y = .2f - .1f * pixelY;
        float z = planeAxis == 0 ? x + .5f : planeAxis == 1 ? -y + .5f : .5f;
        float[] expected = [x, y, z, x + .1f, y, z, x, y - .1f, z];
        for (int index = 0; index < expected.Length; index++)
            Assert.InRange(BitConverter.UInt32BitsToSingle(observed[0][index]), expected[index] - .000001f, expected[index] + .000001f);
    }

    [Theory]
    [InlineData(192f, 0f, 0f, 6, 1)]
    [InlineData(384f, 0f, 0f, 6, 2)]
    [InlineData(768f, 0f, 0f, 6, 3)]
    [InlineData(192f, 1f, 0f, 6, 2)]
    [InlineData(192f, -1f, 0f, 6, 0)]
    [InlineData(192f, .25f, 0f, 6, 1)]
    [InlineData(240f, .25f, 0f, 6, 2)]
    [InlineData(192f, 0f, 10000f, 6, 1)]
    [InlineData(192f, 0f, 0f, 7, 0)]
    public void DirectionalShadowsUseNativeDistanceBiasAndCameraCoordinates(
        float distanceCentimeters, float qualityBias, float translation, int firstLevel, int selectedLevel)
    {
        VirtualShadowView[] views = new VirtualShadowView[4];
        uint[] pages = new uint[4 * 16];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < views.Length; level++)
        {
            float scale = MathF.Pow(2f, -firstLevel - level - 2);
            views[level] = new VirtualShadowView
            {
                LightViewProjection = Matrix4x4.CreateTranslation(-translation, 0, 0) * Matrix4x4.CreateScale(scale, scale, 1),
                VirtualResolution = 512, PageSize = 128, AtlasSize = 512, PhysicalPagesPerRow = 4,
                MaxPhysicalPages = 16, PageTableOffset = (uint)(level * 16), CacheGeneration = 7,
                ClipmapLevel = firstLevel + level, FirstClipmapLevel = firstLevel, ClipmapLevelCount = 4,
                ClipmapWorldOrigin = new(translation, 0, .5f - distanceCentimeters),
                ResolutionLodBias = qualityBias, MipLevelCount = 1, WorldUnitsPerCentimeter = 1,
            };
            int entry = (level * 16 + 10);
            pages[entry] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode((uint)level, 4u);
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(level == selectedLevel ? .75f : .25f),
                    row * 512 + level * 128, 128);
        }
        // Native 192/384/768cm absolute levels 7.x/8.x/9.x and authored bias
        // determine the literal selected rows; common translation retains distance/coverage.
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float> Translation;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadNativeDirectionalShadow()
            {
                float3 position = float3(Translation[0], 0.0, 0.5);
                Visibility[0] = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 4u, 2.0,
                    position, position + float3(1.0, 0.0, 0.0), position + float3(0.0, 1.0, 0.0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadNativeDirectionalShadow", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
             ([BitConverter.SingleToUInt32Bits(translation)], ResourceBindingType.BufferSrv, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
    }

    [Theory]
    [InlineData(0, 1.0f, 0.0f, 0)]
    [InlineData(1, 1.0f, 0.0f, 1)]
    [InlineData(1, 8.0f, 0.0f, 1)]
    [InlineData(3, 1.0f, 8.0f, 3)]
    public void SpotShadowsUseNominalCameraPixelsWhileReceiverDerivativesDescribeThePlane(
        int pixelMip, float stretch, float shear, int selectedMip)
    {
        uint[] view = CreateView(0);
        view[16] = 1024;
        view[34] = 4;
        view[37] = 2;
        view[36] = 4;
        uint[] pages = new uint[85];
        uint[] pixels = new uint[512 * 128];
        int tableOffset = 0;
        for (int mip = 0; mip < 4; mip++)
        {
            int dimension = 8 >> mip;
            int center = dimension / 2;
            int entry = (tableOffset + center * dimension + center);
            pages[entry] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode((uint)mip, 4u);
            tableOffset += dimension * dimension;
            for (int y = 0; y < 128; y++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(mip == selectedMip ? .75f : .25f),
                    y * 512 + mip * 128, 128);
        }
        float step = 1.5f * (1 << pixelMip) / 512;
        // Native camera512px/projection1 at depths .375,.75,1.5,3 gives
        // these radii and light-texel footprints1.5,3,6,12.
        float[] nominalRadii = [.000732421875f, .00146484375f, .0029296875f, .005859375f];
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float3> PixelOffsets;
            StructuredBuffer<float> NominalRadius;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SamplePixelFootprint()
            {
                float3 world = float3(0.0, 0.0, 0.5);
                Result[0] = SampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, NominalRadius[0],
                    world, world + PixelOffsets[0], world + PixelOffsets[1], 1u, 1u, 0.0, 0.0);
            }
            """, "SamplePixelFootprint", new(1, 1, 1),
            [(view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
             ([BitConverter.SingleToUInt32Bits(step), 0u, 0u,
               BitConverter.SingleToUInt32Bits(step * shear), BitConverter.SingleToUInt32Bits(step * stretch), 0u],
              ResourceBindingType.BufferSrv, 12),
             ([BitConverter.SingleToUInt32Bits(nominalRadii[pixelMip])], ResourceBindingType.BufferSrv, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray));
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
            pages[page] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode((uint)page, 4u);
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
            [(view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
             (position, ResourceBindingType.BufferSrv, 8), (new uint[1], ResourceBindingType.BufferUav, 4)],
            new SampledImage(512, 256, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        float visibility = BitConverter.UInt32BitsToSingle(result[0][0]);
        if (mixed) Assert.InRange(visibility, 1.0f / 16, 15.0f / 16);
        else Assert.Equal(0.0f, visibility);
    }

    [Theory]
    [InlineData(-.1f, 0.0f, 1.0f)]
    [InlineData(.1f, 0.0f, 1.0f)]
    [InlineData(-.1f, .0001f, 0.0f)]
    [InlineData(.1f, .0001f, 0.0f)]
    public void SlopedReceiversPreserveVisibilityAcrossMissingFinePages(
        float depthGradient, float occluderSeparation, float expectedVisibility)
    {
        uint[] view = CreateView(0);
        uint[] pages = new uint[21];
        pages[0] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode(0u, 4u);

        pages[20] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode(1u, 4u);
        uint[] pixels = new uint[512 * 128];
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float fineDepth = .5f + (((x + .5f) / 512) - .25f) * depthGradient - occluderSeparation;
            float coarseDepth = .5f + (((x + .5f) / 128) - .25f) * depthGradient - occluderSeparation;
            pixels[y * 512 + x] = BitConverter.SingleToUInt32Bits(1 - fineDepth);
            pixels[y * 512 + 128 + x] = BitConverter.SingleToUInt32Bits(1 - coarseDepth);
        }
        uint[][] result = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            StructuredBuffer<float> Gradient;
            RWStructuredBuffer<float> Result;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void SampleSlopedReceiver()
            {
                VirtualShadowMapParameters view = Views[0];
                view.VirtualResolution = 512u;
                view.MipLevelCount = 3u;
                view.DepthBias = 0.00002;
                float2 uv = float2(127.75 / 512.0, 64.5 / 512.0);
                float depth = 0.5 + (uv.x - 0.25) * Gradient[0];
                float3 world = float3(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth);
                Result[0] = SampleVirtualShadowMapFiltered(view, Pages, Atlas, world,
                    0u, 8u, 16u, 4.0, 8.0, float2(Gradient[0], 0.0));
            }
            """, "SampleSlopedReceiver", new(1, 1, 1),
            [(view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
             ([BitConverter.SingleToUInt32Bits(depthGradient)], ResourceBindingType.BufferSrv, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        Assert.Equal(expectedVisibility, BitConverter.UInt32BitsToSingle(result[0][0]));
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
        uint[] pages = new uint[(lastMipOffset + 1)];
        pages[lastMipOffset] = SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode(3u, 4u);
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
            [(view, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
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
        uint[] pages = new uint[3 * 256];
        uint[] pixels = new uint[512 * 128];
        for (int level = 0; level < 3; level++)
        {
            int entry = (level * 256 + 6 * 16 + 10);
            pages[entry] = level < firstResident ? 0u : SomeEngine.Render.Shadows.VirtualShadowPageEntry.Encode((uint)level, 4u);
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
            [(views, ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (pages, ResourceBindingType.BufferSrv, 4),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        Assert.Equal(firstResident == 3 ? 1u : 0u, result[0][0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectionalCoarseRequestsCoverTheBaseMipOfSelectedClipLevels(bool selectedLevel)
    {
        SomeEngine.Render.Shadows.VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 2048, PageSize = 128,
            AtlasSize = 512, PhysicalPagesPerRow = 4, CacheGeneration = 7,
            ClipmapLevel = 2, ClipmapLevelCount = 3, MipLevelCount = 5,
            Flags = selectedLevel ? SomeEngine.Render.Shadows.VirtualShadowViewFlags.CoarseClipLevel : 0,
            WorldUnitsPerCentimeter = 1,
        }];
        const int pages = 341;
        uint[][] result = new D3D12TestCompute(shaderCompiler).Execute("vsm_mark_coarse_pages.slang", "MarkVirtualShadowCoarsePages", new(1, 1, 1),
            [([1, pages, 9, 0], ResourceBindingType.ConstantBuffer, 0),
             (MemoryMarshal.Cast<SomeEngine.Render.Shadows.VirtualShadowView,uint>(views).ToArray(), ResourceBindingType.BufferSrv, SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), (new uint[pages], ResourceBindingType.BufferUav, 4),
             (new uint[pages], ResourceBindingType.BufferUav, 4), (new uint[pages], ResourceBindingType.BufferUav, 4),
             ([0], ResourceBindingType.BufferUav, 4), (new uint[pages * 2], ResourceBindingType.BufferUav, 8)]);
        int[] expected = selectedLevel ? [119, 120, 135, 136] : [];
        Assert.Equal(expected, Enumerable.Range(0, pages).Where(index => result[0][index] == 9));
        Assert.Equal((uint)expected.Length, result[3][0]);
    }

    private static uint[] CreateView(int level)
    {
        uint[] view = new uint[SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes / sizeof(uint)];
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
        view[39] = BitConverter.SingleToUInt32Bits(1f);
        return view;
    }
}
