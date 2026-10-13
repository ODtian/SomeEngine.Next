using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class SoftShadowSamplingTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void FilteredDistantUmbraProvidesTheDirectionalProjection()
    {
        VirtualShadowView view = new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 8, PageSize = 8,
            AtlasSize = 8, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            ClipmapLevelCount = 2, MipLevelCount = 1, ProjectionDepthScale = -1,
            WorldUnitsPerCentimeter = 100, LightDirection = Vector3.UnitZ,
            Flags = VirtualShadowViewFlags.PrefilteredDistant,
        };
        VirtualShadowView[] views = [view, view with { PageTableOffset = 1, ClipmapLevel = 1 }];
        uint[][] result = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_soft_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Depth;
            Texture2D<float> Filtered;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1,1,1)]
            void ReadDistantUmbra()
            {
                VirtualShadowProjectionResult sample = SampleVirtualShadowDirectionalFilteredSoft(
                    Views, Pages, Depth, Filtered, 0u, 2u, float3(0,0,.5), float3(0,0,-1),
                    0.0, 4u, 8u, 8.0, uint2(0), 0u);
                Result[0] = sample.Valid;
                Result[1] = asuint(sample.Visibility);
                Result[2] = sample.ViewIndex;
            }
            """, "ReadDistantUmbra", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(), ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (new uint[] { 0, VirtualShadowPageEntry.Encode(0, 1) }, ResourceBindingType.BufferSrv, 4),
             (new uint[3], ResourceBindingType.BufferUav, 4)],
            images: [new SampledImage(8, 8, new uint[64], 2, ViewDimension: TextureViewDimension.Texture2DArray),
                new SampledImage(10, 10, new uint[100], 3, Format.R32Float)]);
        Assert.Equal([1u, 0u, 1u], result[0]);
    }

    [Fact]
    public void ASlopedDirectionalReceiverRemainsLitWhenOnlyACoarserMapIsResident()
    {
        VirtualShadowView fine = new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            ClipmapLevelCount = 2, MipLevelCount = 1, ProjectionDepthScale = -1,
            WorldUnitsPerCentimeter = 1, LightDirection = Vector3.UnitZ,
            SourceAngleRadians = .2f,
        };
        VirtualShadowView[] views = [fine, fine with
        {
            LightViewProjection = Matrix4x4.CreateScale(.5f),
            ProjectionDepthScale = -.5f, ClipmapLevel = 1, PageTableOffset = 1,
        }];
        uint[] depth = Enumerable.Range(0, 128 * 128).Select(pixel =>
        {
            float worldX = 4f * ((pixel % 128 + .5f) / 128f) - 2f;
            return BitConverter.SingleToUInt32Bits(1f - .5f * (.75f + .25f * worldX));
        }).ToArray();
        uint[][] observed = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_soft_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(32,1,1)]
            void ReadSlopedCoarseReceiver(uint index : SV_DispatchThreadID)
            {
                float x = (float(index) - 15.5) / 64.0;
                float3 receiver = float3(x, 0, .75 + .25 * x);
                VirtualShadowProjectionResult sample = SampleVirtualShadowSoftView(
                    Views, Pages, Atlas, 0u, 0u, 2u, receiver, normalize(float3(-.25,0,1)),
                    0u, 4u, 8u, 8.0, uint2(index,64), 0u);
                Result[3u * index] = sample.Valid;
                Result[3u * index + 1u] = asuint(sample.Visibility);
                Result[3u * index + 2u] = sample.ViewIndex;
            }
            """, "ReadSlopedCoarseReceiver", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(), ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (new uint[] { 0, VirtualShadowPageEntry.Encode(0, 1) }, ResourceBindingType.BufferSrv, 4),
             (new uint[32 * 3], ResourceBindingType.BufferUav, 4)],
            new SampledImage(128, 128, depth, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        for (int index = 0; index < 32; index++)
        {
            Assert.Equal(1u, observed[0][3 * index]);
            Assert.Equal(1f, BitConverter.UInt32BitsToSingle(observed[0][3 * index + 1]));
            Assert.Equal(1u, observed[0][3 * index + 2]);
        }
    }

    [Fact]
    public void AFiniteDirectionalEmitterProducesAPenumbraAroundADepthEdge()
    {
        VirtualShadowView[] views = [new()
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 128, PageSize = 128,
            AtlasSize = 128, PhysicalPagesPerRow = 1, MaxPhysicalPages = 1,
            ClipmapLevelCount = 1, MipLevelCount = 1, ProjectionDepthScale = -1,
            WorldUnitsPerCentimeter = 1f, LightDirection = Vector3.UnitZ,
            SourceAngleRadians = .2f, CacheGeneration = 7,
        }];
        uint[] depth = Enumerable.Range(0, 128 * 128)
            .Select(pixel => pixel % 128 < 64 ? BitConverter.SingleToUInt32Bits(.75f) : 0u).ToArray();
        uint[][] observed = new D3D12TestCompute(compiler).ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_soft_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Pages;
            Texture2DArray<uint> Atlas;
            RWStructuredBuffer<float> Visibility;
            [shader("compute")]
            [numthreads(32,1,1)]
            void ReadSoftDepthEdge(uint index : SV_DispatchThreadID)
            {
                float3 receiver = float3((float(index) - 15.5) / 64.0, 0.0, .75);
                Visibility[index] = SampleVirtualShadowDirectionalSoft(Views, Pages, Atlas, 0u, 1u,
                    receiver, float3(0,0,-1), 0.0, 0.0, 4u, 8u, 8.0, uint2(index,64), 0u).Visibility;
            }
            """, "ReadSoftDepthEdge", new(1, 1, 1),
            [(MemoryMarshal.Cast<VirtualShadowView, uint>(views).ToArray(), ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             (new uint[] { VirtualShadowPageEntry.Encode(0, 1) }, ResourceBindingType.BufferSrv, 4),
             (new uint[32], ResourceBindingType.BufferUav, 4)],
            new SampledImage(128, 128, depth, 2, ViewDimension: TextureViewDimension.Texture2DArray));
        float[] visibility = observed[0].Select(BitConverter.UInt32BitsToSingle).ToArray();
        Assert.Equal(0f, visibility[0]);
        Assert.Equal(1f, visibility[^1]);
        Assert.Contains(visibility, value => value > 0f && value < 1f);
    }
}
