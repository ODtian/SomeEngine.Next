using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using Xunit;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class ShadowFroxelReceiverTests(D3D12TestCompiler compiler)
{
    [Fact]
    public void OccupiedHiZCellRequestsTheReceiverPagesWithinItsFroxelBounds()
    {
        var compute = new D3D12TestCompute(compiler);
        uint[] depth = Enumerable.Repeat(BitConverter.SingleToUInt32Bits(1f), 256).ToArray();
        depth[0] = BitConverter.SingleToUInt32Bits(.5f);
        uint[][] hiZ = compute.ExecuteImages("hiz_init.slang", "InitHiZ", new(1,1,1), [],
            [new(16,16,depth,0,Format.R32Float,TextureViewDimension.Texture2DArray,Name:"DepthTexture"),
             new(16,16,new uint[256],1,Format.R32Float,TextureViewDimension.Texture2DArray,ResourceBindingType.TextureUav,Name:"Mip0"),
             new(8,8,new uint[64],2,Format.R32Float,TextureViewDimension.Texture2DArray,ResourceBindingType.TextureUav,Name:"Mip1"),
             new(4,4,new uint[16],3,Format.R32Float,TextureViewDimension.Texture2DArray,ResourceBindingType.TextureUav,Name:"Mip2"),
             new(2,2,new uint[4],4,Format.R32Float,TextureViewDimension.Texture2DArray,ResourceBindingType.TextureUav,Name:"Mip3"),
             new(1,1,[0],5,Format.R32Float,TextureViewDimension.Texture2DArray,ResourceBindingType.TextureUav,Name:"Mip4")]);
        var parameters = new VirtualShadowFroxelParameters
        {
            ViewToWorld = Matrix4x4.Identity, ViewTileScaleBias = new(1,-1,-1,1),
            Radius = 1, DepthScale1 = 1, ClipToViewScale = 1, Orthographic = 1,
            Width = 16, Height = 16, Enabled = 1,
        };
        uint[] froxelUniforms = MemoryMarshal.Cast<VirtualShadowFroxelParameters,uint>(new[] { parameters }).ToArray();
        uint[][] froxels = compute.ExecuteImages("vsm_froxel_build.slang", "BuildVirtualShadowFroxels", new(2,2,1),
            [new("FroxelParameters",froxelUniforms,ResourceBindingType.ConstantBuffer,0),
             new("FroxelReceivers",new uint[256 * 8],ResourceBindingType.BufferSrv,32),
             new("FroxelAdditionalReceivers",new uint[8],ResourceBindingType.BufferSrv,32),
             new("OutFroxels",new uint[256 * 2],ResourceBindingType.BufferUav,8),
             new("OutFroxelArguments",new uint[16],ResourceBindingType.BufferUav,4)],
            [new(16,16,hiZ[0],0,Format.R32Float,TextureViewDimension.Texture2DArray,Name:"FroxelHiZ")]);
        var view = new VirtualShadowView
        {
            LightViewProjection = new Matrix4x4(2,0,0,0, 0,2,0,0, 0,0,-1,0, 0,0,0,1),
            VirtualResolution = 32, PageSize = 8, AtlasSize = 16, PhysicalPagesPerRow = 2,
            MaxPhysicalPages = 4, CacheGeneration = 7, MipLevelCount = 1, ClipmapLevelCount = 1,
            ClipmapLevel = 6, FirstClipmapLevel = 6, WorldUnitsPerCentimeter = .01f,
            LightDirection = new(0,0,-1),
        };
        uint[] views = MemoryMarshal.Cast<VirtualShadowView,uint>(new[] { view }).ToArray();
        var demand = new VirtualShadowPageDemandUniforms
        {
            ClipToWorld = new Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,-1,0, 0,0,0,1),
            ReceiverWidth = 16, ReceiverHeight = 16, ViewCount = 1, PageTableEntryCount = 21,
            DirectionalLightCount = 1, DirectionalClipmapLevels = 1, FrameIndex = 1,
            ReceiverProjection = new() { WorldToClipW = new(0,0,0,1), CotanHalfFieldOfView = 1 },
        };
        uint[] uniforms = MemoryMarshal.Cast<VirtualShadowPageDemandUniforms,uint>(new[] { demand }).ToArray();
        uint[] lighting = new uint[48];
        lighting[32] = 16; lighting[33] = 16; lighting[34] = 1; lighting[35] = 1;
        lighting[36] = BitConverter.SingleToUInt32Bits(.01f); lighting[40] = 1; lighting[41] = 1; lighting[46] = 1;
        uint[][] requests = compute.ExecuteImages("vsm_froxel_mark.slang", "MarkVirtualShadowFroxelPages", new(1,1,1),
            [new("PageDemand",uniforms,ResourceBindingType.ConstantBuffer,0),
             new("ShadowViews",views,ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             new("LightingViews",lighting,ResourceBindingType.BufferSrv,192),
             new("LightBuffer",new uint[36],ResourceBindingType.BufferSrv,144),
             new("ClusteredLightGrid",[0,0],ResourceBindingType.BufferSrv,8),
             new("LightIndexList",[0],ResourceBindingType.BufferSrv,4),
             new("DynamicResolutionBias",[0,0],ResourceBindingType.BufferSrv,8),
             new("ReceiverInputs",new uint[256 * 8],ResourceBindingType.BufferSrv,32),
             new("AdditionalReceiverInputs",new uint[8],ResourceBindingType.BufferSrv,32),
             new("RequestedShadowPages",new uint[21],ResourceBindingType.BufferUav,4),
             new("RequestedPageFlags",new uint[21],ResourceBindingType.BufferUav,4),
             new("RequestedPageIndices",new uint[21],ResourceBindingType.BufferUav,4),
             new("RequestedPageCount",[0],ResourceBindingType.BufferUav,4),
             new("PageReceiverMasks",new uint[42],ResourceBindingType.BufferUav,8),
             new("FroxelParameters",froxelUniforms,ResourceBindingType.ConstantBuffer,0),
             new("Froxels",froxels[0],ResourceBindingType.BufferSrv,8),
             new("FroxelArguments",froxels[1],ResourceBindingType.BufferSrv,4)],
            [new(16,16,depth,0,Format.R32Float,TextureViewDimension.Texture2DArray,Name:"ReceiverDepth")]);
        uint[][] coverage = compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Requests;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(4,1,1)]
            void ReadFroxelReceiverCoverage(uint index : SV_DispatchThreadID)
            {
                float3 position = float3((index & 1u) != 0u ? -.0625 : -.4375,
                    (index & 2u) != 0u ? .0625 : .4375, -.5);
                float3 ndc; float2 uv;
                ProjectVirtualShadowPosition(Views[0],position,ndc,uv);
                uint2 page = uint2(uv * float(Views[0].VirtualResolution)) / Views[0].PageSize;
                Coverage[index] = Requests[VirtualShadowPageTableIndex(Views[0],0u,page)] == 1u ? 1u : 0u;
            }
            """, "ReadFroxelReceiverCoverage", new(1,1,1),
            [(views,ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             (requests[0],ResourceBindingType.BufferSrv,4),(new uint[4],ResourceBindingType.BufferUav,4)]);
        // The occupied top-left 8x8 cell spans four visible shadow pages. The
        // four authored plane positions lie within those projected bounds.
        Assert.Equal(new uint[] { 1,1,1,1 }, coverage[0]);
    }
}
