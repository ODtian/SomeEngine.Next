using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Lighting;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowReceiverInputsTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void LocalReceiverCoverageUsesTheEmitterAngleAtItsActualDistance()
    {
        var compute = new D3D12TestCompute(compiler);
        VirtualShadowView[] viewData = Enumerable.Range(0, 12).Select(index => new VirtualShadowView
        {
            LightViewProjection = Matrix4x4.Identity, VirtualResolution = 16, PageSize = 8,
            AtlasSize = 16, PhysicalPagesPerRow = 2, MaxPhysicalPages = 4, CacheGeneration = 7,
            MipLevelCount = 1, LightKind = VirtualShadowLightKind.Point, FaceIndex = (uint)(index % 6),
            PageTableOffset = (uint)(index * 4), WorldUnitsPerCentimeter = .01f, SourceRadius = .01f,
            LightPosition = index < 6 ? new(-.5f,0,.51f) : new(.5f,0,1.5f),
            LightRange = index < 6 ? .02f : 1.01f,
        }).ToArray();
        uint[] views = MemoryMarshal.Cast<VirtualShadowView,uint>(viewData).ToArray();
        var demand = new VirtualShadowPageDemandUniforms
        {
            ClipToWorld = Matrix4x4.Identity, ReceiverWidth = 4, ReceiverHeight = 1,
            ViewCount = 12, PageTableEntryCount = 48, PointLightCount = 2, FrameIndex = 1,
            CullBackfacingPixels = 1,
        };
        uint[] uniforms = MemoryMarshal.Cast<VirtualShadowPageDemandUniforms,uint>(new[] { demand }).ToArray();
        SurfaceReceiver[] receiverData = Enumerable.Repeat(new SurfaceReceiver
        {
            ShadingNormal = new(MathF.Sqrt(.9375f),0,-.25f), GeometricNormal = Vector3.UnitZ,
            Flags = SurfaceReceiverFlags.Present | SurfaceReceiverFlags.Lighting,
        }, 4).ToArray();
        for (int pixel = 0; pixel < receiverData.Length; pixel++)
        {
            receiverData[pixel].Depth = .5f;
            receiverData[pixel].Position = new(pixel < 2 ? -.5f : .5f, 0, .5f);
        }
        uint[] lighting = new uint[48];
        lighting[32] = 2; lighting[33] = lighting[34] = lighting[35] = 1;
        lighting[36] = BitConverter.SingleToUInt32Bits(.01f); lighting[40] = 1;
        lighting[41] = 2; lighting[44] = 2; lighting[45] = lighting[46] = 1;
        uint[][] requests = compute.ExecuteImages("vsm_mark_pages.slang", "MarkVirtualShadowPages", new(1,1,1),
            [new("PageDemand",uniforms,ResourceBindingType.ConstantBuffer,0),
             new("ShadowViews",views,ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             new("LightingViews",lighting,ResourceBindingType.BufferSrv,192),
             new("LightBuffer",new uint[72],ResourceBindingType.BufferSrv,144),
             new("ClusteredLightGrid",[0,2],ResourceBindingType.BufferSrv,8),
             new("LightIndexList",[0,1],ResourceBindingType.BufferSrv,4),
             new("DynamicResolutionBias",[0,0],ResourceBindingType.BufferSrv,8),
             new("ReceiverInputs",MemoryMarshal.Cast<SurfaceReceiver,uint>(receiverData).ToArray(),ResourceBindingType.BufferSrv,SurfaceReceiver.SizeInBytes),
             new("AdditionalReceiverInputs",new uint[12],ResourceBindingType.BufferSrv,SurfaceReceiver.SizeInBytes),
             new("RequestedShadowPages",new uint[48],ResourceBindingType.BufferUav,4),
             new("RequestedPageFlags",new uint[48],ResourceBindingType.BufferUav,4),
             new("RequestedPageIndices",new uint[48],ResourceBindingType.BufferUav,4),
             new("RequestedPageCount",[0],ResourceBindingType.BufferUav,4),
             new("PageReceiverMasks",new uint[96],ResourceBindingType.BufferUav,8)],
            [new(8,1,Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.5f),8).ToArray(),0,
                Format.R32Float,TextureViewDimension.Texture2DArray,Name:"ReceiverDepth")]);
        uint[][] coverage = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Requests;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(2,1,1)]
            void ReadLocalReceiverCoverage(uint index : SV_DispatchThreadID)
            {
                VirtualShadowMapParameters view = Views[index * 6u + 5u];
                float3 ndc; float2 uv;
                ProjectVirtualShadowPosition(view,float3(index == 0u ? -.5 : .5,0,.5),ndc,uv);
                uint2 page = uint2(uv * float(view.VirtualResolution)) / view.PageSize;
                Coverage[index] = Requests[VirtualShadowPageTableIndex(view,0u,page)] == 1u ? 1u : 0u;
            }
            """, "ReadLocalReceiverCoverage", new(1,1,1),
            [(views,ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             (requests[0],ResourceBindingType.BufferSrv,4),(new uint[2],ResourceBindingType.BufferUav,4)]);
        Assert.Equal(new uint[] { 1,0 }, coverage[0]);
    }

    [Fact]
    public void UserReceiversKeepSubsurfaceCoverageAndRejectUnlitBackfaces()
    {
        var compute = new D3D12TestCompute(compiler);
        uint[][] receiverData = compute.ExecuteSource("""
            #include "surface_receiver.slang"
            struct Surface { float3 Normal; uint Model; };
            struct Triangle { float3 Normal; };
            struct ReceiverMaterial
            {
                SurfaceReceiver evaluateReceiver(uint3 pixel, uint instance, uint material,
                    Surface surface, float3 bary, Triangle triangle)
                {
                    SurfaceReceiver value = (SurfaceReceiver)0;
                    value.ShadingNormal = surface.Normal;
                    value.GeometricNormal = triangle.Normal;
                    value.Flags = surface.Model == 2u ? 0u : SURFACE_RECEIVER_LIGHTING;
                    if (surface.Model == 1u) value.Flags |= SURFACE_RECEIVER_SUBSURFACE;
                    return value;
                }
            };
            RWStructuredBuffer<SurfaceReceiver> ReceiverData;
            [shader("compute")]
            [numthreads(8,1,1)]
            void EmitReceivers(uint3 pixel : SV_DispatchThreadID)
            {
                Surface surface;
                surface.Model = pixel.x / 2u;
                surface.Normal = surface.Model < 2u ? float3(0,0,-1) : float3(0,0,1);
                Triangle triangle; triangle.Normal = float3(0,0,1);
                ReceiverMaterial material;
                SurfaceReceiver receiver = material.evaluateReceiver(pixel, 0u, pixel.x, surface, 0.0.xxx, triangle);
                receiver.Flags |= SURFACE_RECEIVER_PRESENT;
                receiver.Depth = .5;
                receiver.Position = float3((float(surface.Model) + .5) / 4.0 * 2.0 - 1.0, 0.0, .5);
                ReceiverData[pixel.x] = receiver;
            }
            """, "EmitReceivers", new(1,1,1), [(new uint[96], ResourceBindingType.BufferUav, SurfaceReceiver.SizeInBytes)]);
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
            CullBackfacingPixels = 1, FirstPersonPixelRequestBias = 2, FirstPersonPixelRequestMinLevel = 8,
        };
        uint[] uniforms = MemoryMarshal.Cast<byte,uint>(MemoryMarshal.AsBytes(new[] { demand }.AsSpan())).ToArray();
        uint[] lighting = new uint[48];
        lighting[32] = 4; lighting[33] = 1; lighting[34] = 1; lighting[35] = 1;
        lighting[36] = BitConverter.SingleToUInt32Bits(.01f); lighting[40] = 1; lighting[41] = 1; lighting[46] = 1;
        uint[][] requests = compute.ExecuteImages("vsm_mark_pages.slang", "MarkVirtualShadowPages", new(1,1,1),
            [new("PageDemand", uniforms, ResourceBindingType.ConstantBuffer, 0),
             new("ShadowViews", views, ResourceBindingType.BufferSrv, VirtualShadowView.SizeInBytes),
             new("LightingViews", lighting, ResourceBindingType.BufferSrv, 192),
             new("LightBuffer", new uint[36], ResourceBindingType.BufferSrv, 144),
             new("ClusteredLightGrid", [0,0], ResourceBindingType.BufferSrv, 8),
             new("LightIndexList", [0], ResourceBindingType.BufferSrv, 4),
             new("DynamicResolutionBias", [0,0], ResourceBindingType.BufferSrv, 8),
             new("ReceiverInputs", receiverData[0], ResourceBindingType.BufferSrv, SurfaceReceiver.SizeInBytes),
             new("AdditionalReceiverInputs", new uint[12], ResourceBindingType.BufferSrv, SurfaceReceiver.SizeInBytes),
             new("RequestedShadowPages", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageFlags", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageIndices", new uint[21], ResourceBindingType.BufferUav, 4),
             new("RequestedPageCount", [0], ResourceBindingType.BufferUav, 4),
             new("PageReceiverMasks", new uint[42], ResourceBindingType.BufferUav, 8)],
            [new(8,1,Enumerable.Repeat(BitConverter.SingleToUInt32Bits(.5f),8).ToArray(),0,
                Format.R32Float,TextureViewDimension.Texture2DArray, Name: "ReceiverDepth")]);
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
        Assert.Equal(new uint[] { 0,1,0,1 }, coverage[0]);
    }
}
