using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Render.Shadows;
using static SomeEngine.Graphics.Direct3D12.Tests.D3D12TestCompute;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal sealed class ShadowReceiverRequests(D3D12TestCompiler compiler)
{
    private readonly D3D12TestCompute _compute = new(compiler);

    internal uint[] Execute(VirtualShadowView view, string source, string entry,
        DispatchArguments dispatch, params SampledImage[] images)
    {
        uint tableEntries = view.VirtualResolution / view.PageSize;
        tableEntries *= tableEntries;
        var demand = new VirtualShadowPageDemandUniforms
        {
            ViewCount = 1, PageTableEntryCount = tableEntries, FrameIndex = 1,
            ReceiverProjection = new() { WorldToClipW = new(0,0,0,1), CotanHalfFieldOfView = 1 },
        };
        uint[][] requests = _compute.ExecuteSourceImages("#define VSM_WHOLE_PAGE_SAMPLING 1\n#include \"vsm_receiver_requests.slang\"\n" + source,
            entry, dispatch,
            [new("PageDemand",MemoryMarshal.Cast<VirtualShadowPageDemandUniforms,uint>(new[] { demand }).ToArray(),ResourceBindingType.ConstantBuffer,0),
             new("ShadowViews",MemoryMarshal.Cast<VirtualShadowView,uint>(new[] { view }).ToArray(),ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             new("LightingViews",new uint[48],ResourceBindingType.BufferSrv,192),
             new("LightBuffer",new uint[36],ResourceBindingType.BufferSrv,144),
             new("ClusteredLightGrid",[0,0],ResourceBindingType.BufferSrv,8),
             new("LightIndexList",[0],ResourceBindingType.BufferSrv,4),
             new("DynamicResolutionBias",[0],ResourceBindingType.BufferSrv,4),
             new("RequestedPageFlags",new uint[tableEntries],ResourceBindingType.BufferUav,4),
             new("RequestedPageIndices",new uint[tableEntries],ResourceBindingType.BufferUav,4),
             new("RequestedPageCount",[0],ResourceBindingType.BufferUav,4)],
            [.. images, ShadowTestImages.ReceiverMask(view.VirtualResolution / view.PageSize,
                view.VirtualResolution / view.PageSize * 2)]);
        return requests[0];
    }

    internal uint[] ReadCoverage(VirtualShadowView view, uint[] requests, Vector3[] positions)
    {
        uint[][] coverage = _compute.ExecuteSource("""
            #define VSM_WHOLE_PAGE_SAMPLING 1
            #include "vsm_sampling.slang"
            StructuredBuffer<VirtualShadowMapParameters> Views;
            StructuredBuffer<uint> Requests;
            StructuredBuffer<float3> Positions;
            RWStructuredBuffer<uint> Coverage;
            [shader("compute")]
            [numthreads(1,1,1)]
            void ReadReceiverCoverage(uint index : SV_DispatchThreadID)
            {
                float3 ndc; float2 uv;
                ProjectVirtualShadowPosition(Views[0],Positions[index],ndc,uv);
                uint2 page = uint2(uv * float(Views[0].VirtualResolution)) / Views[0].PageSize;
                Coverage[index] = Requests[VirtualShadowPageTableIndex(Views[0],0u,page)] != 0u ? 1u : 0u;
            }
            """, "ReadReceiverCoverage", new((uint)positions.Length,1,1),
            [(MemoryMarshal.Cast<VirtualShadowView,uint>(new[] { view }).ToArray(),ResourceBindingType.BufferSrv,VirtualShadowView.SizeInBytes),
             (requests,ResourceBindingType.BufferSrv,4),
             (MemoryMarshal.Cast<Vector3,uint>(positions).ToArray(),ResourceBindingType.BufferSrv,12),
             (new uint[positions.Length],ResourceBindingType.BufferUav,4)]);
        return coverage[0];
    }
}
