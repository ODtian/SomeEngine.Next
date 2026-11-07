#ifdef CLUSTER_TRANSPARENCY_HARDWARE_ENTRIES
import "cluster_transparency";

[shader("vertex")]
TransparencyHardwareVertex DrawTransparentCoverage(uint vertex : SV_VertexID)
{ return MakeTransparencyHardwareVertex(vertex); }

[shader("fragment")] [earlydepthstencil]
void StoreTransparentCoverage(TransparencyHardwareVertex input)
{ StoreTransparencyHardwareCoverage(input); }
#else
#ifndef CLUSTER_TRANSPARENCY_HARDWARE_SLANG
#define CLUSTER_TRANSPARENCY_HARDWARE_SLANG
struct TransparencyHardwareVertex
{
    float4 Position : SV_Position;
    nointerpolation uint Triangle : TEXCOORD0;
    uint View : SV_RenderTargetArrayIndex;
};

TransparencyHardwareVertex MakeTransparencyHardwareVertex(uint vertex)
{
    TransparencyTriangle triangle = TransparencyRasterTriangles[vertex / 3u];
    uint corner = vertex % 3u;
    float2 point = corner == 0u ? triangle.First : corner == 1u ? triangle.Second : triangle.Third;
    TransparencyHardwareVertex output;
    output.Position = float4(point / float2(TransparencyUniforms.Width, TransparencyUniforms.Height) * float2(2,-2) + float2(-1,1), triangle.Depths[corner], 1);
    output.Triangle = vertex / 3u; output.View = triangle.View;
    return output;
}

void StoreTransparencyHardwareCoverage(TransparencyHardwareVertex input)
{
    uint2 pixel = uint2(input.Position.xy);
    ClusteredLightGridParameters grid = TransparencyLightingViews[input.View];
    uint4 range = TransparencyCoverageRanges[input.Triangle];
    uint2 tile = pixel / uint2(grid.TileSizeX, grid.TileSizeY);
    uint association = range.x + (tile.y - range.z) * range.w + tile.x - range.y;
    uint bit = (pixel.y & 15u) * 16u + (pixel.x & 15u);
    uint address = association * 8u + bit / 32u;
    uint mask = 1u << (bit & 31u);
    __target_switch
    {
    case spirv:
        // Vulkan helpers participate in subgroups; each covered fragment writes its mask.
        InterlockedOr(TransparencyHardwareMasks[address],mask);
        break;
    case _sm_6_5:
        {
            uint4 matching = WaveMatch(address);
            uint coverage = WaveMultiPrefixBitOr(mask,matching) | mask;
            if (WaveMaskPrefix(matching,WaveGetLaneIndex()) + 1u == WaveMaskBitCount(matching))
                InterlockedOr(TransparencyHardwareMasks[address],coverage);
        }
        break;
    default:
        InterlockedOr(TransparencyHardwareMasks[address],mask);
        break;
    }
}

void CountTransparencyHardwareCells(uint3 thread)
{
    uint index = TransparencyWork(thread);
    if (index >= TransparencyCellWork.Load(8u)) return;
    uint coverage = 0u;
    [unroll] for (uint word = 0u; word < 8u; word++) coverage |= TransparencyHardwareMasks[index * 8u + word];
    if (coverage == 0u) return;
    uint2 association = TransparencyHardwareAssociations[index];
    TransparencyHardwareAssociations[index].x = association.x | 0x80000000u;
    uint cell = association.y;
    uint4 matching = WaveMatchingLanes(cell);
    if (WaveGetLaneIndex() == WaveMaskFirstLane(matching))
    {
        uint count;
        InterlockedAdd(TransparencyTileCounts[cell],WaveMaskBitCount(matching),count);
        if (count == 0u)
        {
            uint job;
            TransparencyCellWork.InterlockedAdd(0u,1u,job);
            TransparencyCellWork.Store(16u + job * 4u,cell);
            TransparencyCellWork.InterlockedAdd(12u,1u,job);
            TransparencyCellWork.Store(16u + (TransparencyUniforms.CellCount + job) * 4u,cell);
        }
    }
}

void ClearTransparencyHardwareMasks(uint3 thread)
{
    uint work = TransparencyWork(thread);
    if (work < TransparencyCellWork.Load(8u) * 8u) TransparencyHardwareMasks[work] = 0u;
}

void ScatterTransparencyHardwareCells(uint3 thread)
{
    uint index = TransparencyWork(thread);
    if (index >= TransparencyCellWork.Load(8u)) return;
    uint2 association = TransparencyHardwareAssociations[index];
    if ((association.x & 0x80000000u) == 0u || TransparencyCellStreams[association.y] != 0u) return;
    uint output;
    InterlockedAdd(TransparencyCellWrites[association.y],1u,output);
    TransparencyReferences[TransparencyCellOffsets[association.y] + output] = association.x & 0x7FFFFFFFu;
}
#endif
#endif
