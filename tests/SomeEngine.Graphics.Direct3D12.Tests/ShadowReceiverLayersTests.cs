using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowReceiverLayersTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void UserExtraDepthRetainsCurrentAndPreviousReceiverCoverage()
    {
        var compute = new D3D12TestCompute(compiler);
        uint[][] receiverData = compute.ExecuteSource("""
            #include "surface_receiver.slang"
            Texture2DArray<float> PreviousDepth;
            RWStructuredBuffer<SurfaceReceiver> PrimaryReceivers;
            RWStructuredBuffer<SurfaceReceiver> ExtraReceivers;
            struct Surface { uint Pixel; };
            struct Triangle { float3 Normal; };
            struct LayerMaterial
            {
                SurfaceReceiver evaluateReceiver(uint3 pixel, uint instance, uint material,
                    Surface surface, float3 bary, Triangle triangle)
                {
                    SurfaceReceiver primary = (SurfaceReceiver)0;
                    primary.ShadingNormal = primary.GeometricNormal = triangle.Normal;
                    primary.Flags = SURFACE_RECEIVER_PRESENT;
                    primary.Depth = .5;
                    primary.Position = float3(-.75 + .5 * float(pixel.x / 2u),0,.5);
                    SurfaceReceiver extra = primary;
                    extra.Flags |= SURFACE_RECEIVER_LIGHTING;
                    if (pixel.x / 2u == 0u)
                    {
                        extra.Depth = .5;
                        extra.Flags |= SURFACE_RECEIVER_WATER;
                        WriteSurfaceReceiverLayer(ExtraReceivers,pixel,uint2(8,1),1u,0u,extra);
                    }
                    if (pixel.x / 2u == 1u)
                    {
                        float4x4 currentToPrevious = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, .5,0,0,1);
                        uint2 previousPixel;
                        float depth;
                        if (TrySurfaceReceiverPreviousPixel(pixel.xy,uint2(8,1),.5,currentToPrevious,
                                uint2(8,1),float4(0,0,1,1),previousPixel) &&
                            TryReadSurfaceReceiverPreviousDepth(PreviousDepth,previousPixel,0u,1.0,depth))
                        {
                            extra.Depth = depth;
                            extra.Flags |= SURFACE_RECEIVER_TRANSLUCENCY;
                            WriteSurfaceReceiverLayer(ExtraReceivers,pixel,uint2(8,1),1u,1u,extra);
                        }
                    }
                    if (pixel.x / 2u == 2u)
                    {
                        extra.Depth = .5;
                        extra.Flags |= SURFACE_RECEIVER_HAIR;
                        WriteSurfaceReceiverLayer(ExtraReceivers,pixel,uint2(8,1),1u,2u,extra);
                    }
                    return primary;
                }
            };
            [shader("compute")]
            [numthreads(8,1,1)]
            void EmitReceivers(uint3 pixel : SV_DispatchThreadID)
            {
                LayerMaterial material;
                Surface surface; surface.Pixel = pixel.x;
                Triangle triangle; triangle.Normal = float3(0,0,1);
                PrimaryReceivers[pixel.x] = material.evaluateReceiver(pixel,0u,pixel.x,surface,0.0.xxx,triangle);
            }
            """, "EmitReceivers", new(1,1,1),
            [(new uint[96],ResourceBindingType.BufferUav,SurfaceReceiver.SizeInBytes),(new uint[288],ResourceBindingType.BufferUav,SurfaceReceiver.SizeInBytes)],
            new SampledImage(8,1,new[] { .9f,.9f,.8f,.8f,.5f,.5f,.7f,.7f }.Select(BitConverter.SingleToUInt32Bits).ToArray(),0,Format.R32Float,TextureViewDimension.Texture2DArray));
        var view = new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 32, PageSize = 8, AtlasSize = 16,
            PhysicalPagesPerRow = 2, MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1,
            ClipmapLevelCount = 1, ClipmapLevel = 6, FirstClipmapLevel = 6, WorldUnitsPerCentimeter = .01f, LightDirection = new Vector3(0,0,-1),
        };
        uint[] views = MemoryMarshal.Cast<byte,uint>(MemoryMarshal.AsBytes(new[] { view }.AsSpan())).ToArray();
        var demand = new VirtualShadowPageDemandUniforms
        {
            ClipToWorld = Matrix4x4.Identity, ReceiverWidth = 8, ReceiverHeight = 1, ViewCount = 1,
            PageTableEntryCount = 21, DirectionalLightCount = 1, DirectionalClipmapLevels = 1, FrameIndex = 1,
            AdditionalReceiverLayerCount = 3, CullBackfacingPixels = 1, FirstPersonPixelRequestBias = 2, FirstPersonPixelRequestMinLevel = 8,
        };
        uint[] uniforms = MemoryMarshal.Cast<byte,uint>(MemoryMarshal.AsBytes(new[] { demand }.AsSpan())).ToArray();
        uint[] lighting = new uint[48];
        lighting[32] = 8; lighting[33] = 1; lighting[34] = 1; lighting[35] = 1;
        lighting[36] = BitConverter.SingleToUInt32Bits(.01f); lighting[40] = 1; lighting[41] = 1; lighting[46] = 1;
        uint[][] requests = compute.ExecuteImages("vsm_mark_pages.slang", "MarkVirtualShadowPages", new(1,1,1),
            [new("PageDemand", uniforms, ResourceBindingType.ConstantBuffer, 0),
             new("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new("LightingViews", lighting, ResourceBindingType.BufferSrv, 192),
             new("LightBuffer", new uint[36], ResourceBindingType.BufferSrv, 144),
             new("ClusteredLightGrid", [0,0], ResourceBindingType.BufferSrv, 8),
             new("LightIndexList", [0], ResourceBindingType.BufferSrv, 4),
             new("DynamicResolutionBias", [0], ResourceBindingType.BufferSrv, 4),
             new("ReceiverInputs", receiverData[0], ResourceBindingType.BufferSrv, SurfaceReceiver.SizeInBytes),
             new("AdditionalReceiverInputs", receiverData[1], ResourceBindingType.BufferSrv, SurfaceReceiver.SizeInBytes),
             new("RequestedShadowPages", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageFlags", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageIndices", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageCount", [0], ResourceBindingType.BufferUav, 4),
            ],
            [new(8,1,Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.5f),8).ToArray(),0,
                Format.R32Float,TextureViewDimension.Texture2DArray, Name: "ReceiverDepth"),
             ShadowTestImages.ReceiverMask(8, 12)]);
        uint[][] coverage = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Requests;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(4,1,1)]
            void ReadReceiverCoverage(uint3 pixel : SV_DispatchThreadID)
            {
                float3 position = float3(-.75 + .5 * pixel.x, 0.0, .5);
                float3 ndc; float2 uv;
                ProjectVirtualShadowPosition(Views[0], position, ndc, uv);
                uint2 page = uint2(uv * float(Views[0].VirtualResolution)) / Views[0].PageSize;
                Coverage[pixel.x] = Requests[VirtualShadowPageTableIndex(Views[0],0u,page)] == 1u ? 1u : 0u;
            }
            """, "ReadReceiverCoverage", new(1,1,1),
            [(views,ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             (requests[0],ResourceBindingType.BufferSrv,4),(new uint[4],ResourceBindingType.BufferUav,4)]);
        Assert.Equal(new uint[] { 1,1,1,0 }, coverage[0]);
    }
}
