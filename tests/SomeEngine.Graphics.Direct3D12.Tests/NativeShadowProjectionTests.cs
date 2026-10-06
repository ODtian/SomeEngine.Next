using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class NativeShadowProjectionTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void SpotSamplingUsesEvenMipLevelsWhenConfigured()
    {
        // Native viewport250, camera projection scale1 and depth1 give radius.004.
        // Light scale1/depth1 and virtual1024 give footprint8.192, hence mip3; Native even mode then selects mip2.
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = new(1,0,0,0, 0,1,0,0, 0,0,-1.001001f,-1, 0,0,-.1001001f,0),
            VirtualResolution = 1024, PageSize = 128, AtlasSize = 512,
            PhysicalPagesPerRow = 4, MaxPhysicalPages = 16, CacheGeneration = 7,
            MipLevelCount = 4, LightKind = VirtualShadowLightKind.Spot,
            Flags = VirtualShadowViewFlags.UseReceiverMask | VirtualShadowViewFlags.EvenMipLevels,
        }];
        uint[] pages = new uint[85 * 4];
        uint[] pixels = new uint[512 * 128];
        int[] entries = [36, 74, 83, 84];
        uint[] pageCoordinates = [4, 2, 1, 0];
        for (int mip = 0; mip < 4; mip++)
        {
            int entry = entries[mip] * 4;
            pages[entry] = (uint)mip + 1;
            pages[entry + 1] = pages[entry + 2] = pageCoordinates[mip];
            pages[entry + 3] = 7;
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(mip == 2 ? .75f : .01f),
                    row * 512 + mip * 128, 128);
        }
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadNominalSpotShadow()
            {
                float3 position = float3(0.0, 0.0, -1.0);
                Visibility[0] = SampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, 0.004,
                    position, position + float3(0.0001, 0.0, 0.0), position + float3(0.0, 0.0001, 0.0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadNominalSpotShadow", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes),
             (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
    }

    [Fact]
    public void SpotSamplingUsesTheNominalCameraPixelRadius()
    {
        // Native viewport250, camera projection scale1 and depth1 give radius.004.
        // Light scale1/depth1 and virtual1024 give footprint8.192, hence mip3.
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = new(1,0,0,0, 0,1,0,0, 0,0,-1.001001f,-1, 0,0,-.1001001f,0),
            VirtualResolution = 1024, PageSize = 128, AtlasSize = 512,
            PhysicalPagesPerRow = 4, MaxPhysicalPages = 16, CacheGeneration = 7,
            MipLevelCount = 4, LightKind = VirtualShadowLightKind.Spot,
            Flags = VirtualShadowViewFlags.UseReceiverMask,
        }];
        uint[] pages = new uint[85 * 4];
        uint[] pixels = new uint[512 * 128];
        int[] entries = [36, 74, 83, 84];
        uint[] pageCoordinates = [4, 2, 1, 0];
        for (int mip = 0; mip < 4; mip++)
        {
            int entry = entries[mip] * 4;
            pages[entry] = (uint)mip + 1;
            pages[entry + 1] = pages[entry + 2] = pageCoordinates[mip];
            pages[entry + 3] = 7;
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(mip == 3 ? .75f : .01f),
                    row * 512 + mip * 128, 128);
        }
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadNominalSpotShadow()
            {
                float3 position = float3(0.0, 0.0, -1.0);
                Visibility[0] = SampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, 0.004,
                    position, position + float3(0.0001, 0.0, 0.0), position + float3(0.0, 0.0001, 0.0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadNominalSpotShadow", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes),
             (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
    }

    [Fact]
    public void DirectionalSamplingUsesTheBiasOfTheUnbiasedDistanceLevel()
    {
        // The native source uses centimeters. 192 lies between 128 and 256,
        // so its unbiased absolute level is seven and its first-level-six
        // index is one. That view's bias of one selects index two.
        // The unrelated first view has a bias of five.
        VirtualShadowView[] views = new VirtualShadowView[4];
        uint[] pages = new uint[4 * 16 * 4];
        uint[] pixels = new uint[512 * 128];
        for (int index = 0; index < views.Length; index++)
        {
            views[index] = new VirtualShadowView
            {
                LightViewProjection = Matrix4x4.CreateScale(1f / (256 << index), 1f / (256 << index), 1f),
                VirtualResolution = 512,
                PageSize = 128,
                AtlasSize = 512,
                PhysicalPagesPerRow = 4,
                MaxPhysicalPages = 16,
                PageTableOffset = (uint)(index * 16),
                CacheGeneration = 7,
                ClipmapLevel = index + 6,
                ClipmapWorldOrigin = new(0f, 0f, -191.5f),
                ResolutionLodBias = index == 0 ? 5f : index == 1 ? 1f : 0f,
                FirstClipmapLevel = 6,
                WorldUnitsPerCentimeter = 1f,
                ClipmapLevelCount = 4,
                MipLevelCount = 1,
            };
            int entry = (index * 16 + 10) * 4;
            pages[entry] = (uint)index + 1;
            pages[entry + 1] = pages[entry + 2] = 2;
            pages[entry + 3] = 7;
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(index == 2 ? .75f : .25f),
                    row * 512 + index * 128, 128);
        }
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadDirectionalShadow()
            {
                float3 position = float3(0.0, 0.0, 0.5);
                Visibility[0] = SampleDirectionalVirtualShadowMap(Views, Pages, Atlas, 0u, 4u, 0.0,
                    position, position + float3(1.0, 0.0, 0.0), position + float3(0.0, 1.0, 0.0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadDirectionalShadow", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes),
             (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
    }

    [Fact]
    public void SpotSamplingUsesMipZeroWhenTheReceiverMaskIsDisabled()
    {
        // Native receiver-mask-off sampling starts at mip zero. Its authored
        // depth shadows this receiver; all three coarser mips contain open space.
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity,
            VirtualResolution = 1024,
            PageSize = 128,
            AtlasSize = 512,
            PhysicalPagesPerRow = 4,
            MaxPhysicalPages = 16,
            CacheGeneration = 7,
            MipLevelCount = 4,
            LightKind = VirtualShadowLightKind.Spot,
        }];
        uint[] pages = new uint[85 * 4];
        uint[] pixels = new uint[512 * 128];
        int[] entries = [36, 74, 83, 84];
        uint[] pageCoordinates = [4, 2, 1, 0];
        for (int mip = 0; mip < 4; mip++)
        {
            int entry = entries[mip] * 4;
            pages[entry] = (uint)mip + 1;
            pages[entry + 1] = pages[entry + 2] = pageCoordinates[mip];
            pages[entry + 3] = 7;
            for (int row = 0; row < 128; row++)
                Array.Fill(pixels, BitConverter.SingleToUInt32Bits(mip == 0 ? .75f : .25f),
                    row * 512 + mip * 128, 128);
        }
        uint[][] observed = new D3D12TestCompute(shaderCompiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint4> Pages;
            Texture2D<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ReadSpotShadow()
            {
                float3 position = float3(0.0, 0.0, 0.5);
                Visibility[0] = SampleSpotVirtualShadowMap(Views, Pages, Atlas, 0u, 0.0, 0.0,
                    position, position + float3(0.03125, 0.0, 0.0), position + float3(0.0, 0.03125, 0.0),
                    1u, 1u, 0.0, 0.0);
            }
            """, "ReadSpotShadow", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views.AsSpan()).ToArray(), ResourceBindingType.BufferSrv,
                VirtualShadowView.SizeInBytes),
             (pages, ResourceBindingType.BufferSrv, 16),
             (new uint[1], ResourceBindingType.BufferUav, 4)], new SampledImage(512, 128, pixels, 2));
        Assert.Equal(0f, BitConverter.UInt32BitsToSingle(observed[0][0]));
    }
}
