#ifndef GI_PROBE_LAYOUT_SLANG
#define GI_PROBE_LAYOUT_SLANG

RWStructuredBuffer<uint2> GiProbeTileRanges;
RWStructuredBuffer<uint> GiProbeTileCounts;
RWStructuredBuffer<float2> GiProbeSamples;
RWStructuredBuffer<uint> GiProbeReprojection;
RWStructuredBuffer<float4> GiFilteredProbeRadiance;

uint2 GiProbeTileRange(uint tile, uint layer)
{
    return GiProbeTileRanges[layer * (GiTileCount(GiSettings) + 1u) + tile];
}

bool GiViewChanged(uint view)
{
    ClusterViewUniforms value = GiViews[view];
    return any(value.View[0] != value.PrevView[0]) || any(value.View[1] != value.PrevView[1]) ||
        any(value.View[2] != value.PrevView[2]) || any(value.View[3] != value.PrevView[3]) ||
        any(value.WorldOriginHigh != value.PreviousWorldOriginHigh) || any(value.WorldOriginLow != value.PreviousWorldOriginLow) ||
        value.P00 != value.PrevP00 || value.P11 != value.PrevP11;
}

bool GiLayoutChanged()
{
    if (GiSettings.UseHistory == 0u || GiSettings.SceneChanged != 0u) return true;
    for (uint view = 0u; view < GiSettings.ViewCount; view++) if (GiViewChanged(view)) return true;
    return false;
}

GiProbeSurface ReadGiProbeSurface(uint3 pixel)
{
    GiProbeSurface surface = (GiProbeSurface)0;
    if (IsVisBufferEmpty(GiVisibility.Load(int4(pixel, 0))) || GiSurfaceInstances.Load(int4(pixel, 0)) == 0u) return surface;
    surface.Position = float4(GiWorldPosition(pixel), 1.0);
    surface.Normal = float4(GiPlaneNormal(pixel), GiPixelFootprint(surface.Position.xyz, pixel.z));
    surface.Instance = GiSurfaceInstances.Load(int4(pixel, 0)) - 1u;
    surface.Seed = surface.Instance * 157u + (pixel.x + pixel.y * GiSettings.Width) * 179u;
    surface.Pixel = pixel.xy;
    surface.Tile = (pixel.z * GiSettings.ProbeHeight + pixel.y / GI_PROBE_SPACING) * GiSettings.ProbeWidth + pixel.x / GI_PROBE_SPACING;
    return surface;
}

uint GiCoveragePixel(uint tile)
{
    uint bits = firstbithigh(GiSettings.Width * GiSettings.Height * GiSettings.ViewCount) + 1u;
    uint value = GiProbeRequests[(GiSettings.HistoryLayer ^ 1u) * GiTileCount(GiSettings) + tile] & ((1u << bits) - 1u);
    return value == 0u ? 0xffffffffu : value - 1u;
}

uint3 GiSurfacePixel(uint index)
{
    return uint3(index % GiSettings.Width, index / GiSettings.Width % GiSettings.Height,
        index / (GiSettings.Width * GiSettings.Height));
}

groupshared uint GiTileNormals, GiTilePlanes, GiTileAnchor;
[shader("compute")]
[numthreads(64, 1, 1)]
void ClassifyGiProbeTiles(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    if (!GiLayoutChanged()) return;
    uint view = group.x / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint tile = group.x % (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint2 corner = uint2(tile % GiSettings.ProbeWidth, tile / GiSettings.ProbeWidth) * GI_PROBE_SPACING;
    if (lane == 0u) { GiTileNormals = 0u; GiTilePlanes = 0u; GiTileAnchor = 0xffffffffu; }
    GroupMemoryBarrierWithGroupSync();
    for (uint sample = lane; sample < 256u; sample += 64u)
    {
        uint2 pixel = corner + uint2(sample % 16u, sample / 16u);
        if (any(pixel >= uint2(GiSettings.Width, GiSettings.Height))) continue;
        if (ReadGiProbeSurface(uint3(pixel, view)).Position.w > 0.0)
            InterlockedMin(GiTileAnchor, pixel.x + pixel.y * GiSettings.Width);
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint sample = lane; sample < 256u; sample += 64u)
    {
        uint2 pixel = corner + uint2(sample % 16u, sample / 16u);
        if (any(pixel >= uint2(GiSettings.Width, GiSettings.Height))) continue;
        GiProbeSurface source = ReadGiProbeSurface(uint3(pixel, view));
        if (source.Position.w == 0.0) continue;
        GiProbeSurface anchor = ReadGiProbeSurface(uint3(GiTileAnchor % GiSettings.Width, GiTileAnchor / GiSettings.Width, view));
        uint2 bin = uint2(clamp((EncodeGiNormal(source.Normal.xyz) * 0.5 + 0.5) * 4.0, 0.0.xx, 3.0.xx));
        InterlockedOr(GiTileNormals, 1u << (bin.x + bin.y * 4u));
        float plane = dot(source.Position.xyz - anchor.Position.xyz, anchor.Normal.xyz);
        uint planeBin = uint(clamp(round(plane / (source.Normal.w * 4.0)) + 16.0, 0.0, 31.0));
        InterlockedOr(GiTilePlanes, 1u << planeBin);
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u) GiProbeTileCounts[group.x] = min(4u, max(countbits(GiTileNormals), countbits(GiTilePlanes)));
}

[shader("compute")]
[numthreads(1, 1, 1)]
void AllocateGiProbeTiles()
{
    uint count = GiTileCount(GiSettings), current = GiSettings.HistoryLayer * (count + 1u);
    if (!GiLayoutChanged())
    {
        uint previous = (GiSettings.HistoryLayer ^ 1u) * (count + 1u);
        for (uint tile = 0u; tile <= count; tile++) GiProbeTileRanges[current + tile] = GiProbeTileRanges[previous + tile];
        return;
    }
    uint offset = 0u;
    for (uint tile = 0u; tile < count; tile++)
    {
        uint allocated = GiProbeTileCounts[tile];
        GiProbeTileRanges[current + tile] = uint2(offset, allocated);
        offset += allocated;
    }
    GiProbeTileRanges[current + count] = uint2(offset, 0u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiProbeSources(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiProbeCount()) GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + thread.x] = (GiProbeSurface)0;
    if (thread.x < GiTileCount(GiSettings)) GiProbeRequests[GiSettings.HistoryLayer * GiTileCount(GiSettings) + thread.x] = 0u;
}

groupshared GiProbeSurface GiPlacementSurfaces[256];
groupshared float GiPlacementScores[64];
groupshared uint GiPlacementIndices[64];
groupshared GiProbeSurface GiPlacedSurface;
[shader("compute")]
[numthreads(64, 1, 1)]
void PlaceGiScreenProbes(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint2 range = GiProbeTileRange(group.x, GiSettings.HistoryLayer);
    if (range.y == 0u) return;
    uint view = group.x / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint tile = group.x % (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint2 corner = uint2(tile % GiSettings.ProbeWidth, tile / GiSettings.ProbeWidth) * 16u;
    float errors[4];
    for (uint sample = lane; sample < 256u; sample += 64u)
    {
        uint2 pixel = min(corner + uint2(sample % 16u, sample / 16u), uint2(GiSettings.Width, GiSettings.Height) - 1u);
        GiPlacementSurfaces[sample] = ReadGiProbeSurface(uint3(pixel, view));
        errors[sample / 64u] = 1e20;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint probe = 0u; probe < range.y; probe++)
    {
        float score = 0.0;
        uint selected = 0u;
        for (uint sample = lane; sample < 256u; sample += 64u)
        {
            GiProbeSurface value = GiPlacementSurfaces[sample];
            if (value.Position.w == 0.0) continue;
            float candidate;
            if (probe == 0u)
            {
                float2 center = float2(sample % 16u, sample / 16u) + 0.5 - 8.0;
                candidate = 1.0 / (1.0 + dot(center, center));
            }
            else
            {
                if (GiPlacedSurface.Position.w > 0.0)
                {
                    float3 delta = value.Position.xyz - GiPlacedSurface.Position.xyz;
                    float radius = value.Normal.w * 16.0;
                    float angle = (1.0 - max(0.0, dot(value.Normal.xyz, GiPlacedSurface.Normal.xyz))) * 5.0;
                    float plane = abs(dot(delta, GiPlacedSurface.Normal.xyz)) / max(0.003, value.Normal.w * 1.5);
                    errors[sample / 64u] = min(errors[sample / 64u], max(angle, max(plane, length(delta) / radius)));
                }
                candidate = errors[sample / 64u];
            }
            if (candidate > score) { score = candidate; selected = sample; }
        }
        GiPlacementScores[lane] = score; GiPlacementIndices[lane] = selected;
        GroupMemoryBarrierWithGroupSync();
        for (uint stride = 32u; stride > 0u; stride /= 2u)
        {
            if (lane < stride && GiPlacementScores[lane + stride] > GiPlacementScores[lane])
            {
                GiPlacementScores[lane] = GiPlacementScores[lane + stride];
                GiPlacementIndices[lane] = GiPlacementIndices[lane + stride];
            }
            GroupMemoryBarrierWithGroupSync();
        }
        if (lane == 0u)
        {
            GiProbeSurface value = GiPlacementSurfaces[GiPlacementIndices[0]];
            if (GiPlacementScores[0] <= (probe == 0u ? 0.0 : 1.0)) value.Position.w = 0.0;
            GiPlacedSurface = value;
            GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + range.x + probe] = value;
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u)
    {
        uint pixel = GiCoveragePixel(group.x);
        if (pixel != 0xffffffffu)
            GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + range.x + range.y - 1u] = ReadGiProbeSurface(GiSurfacePixel(pixel));
    }
}

#endif
