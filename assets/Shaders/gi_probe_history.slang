#ifndef GI_PROBE_HISTORY_SLANG
#define GI_PROBE_HISTORY_SLANG

bool ReprojectGiProbeSurface(GiProbeSurface anchor, out GiProbeSurface current)
{
    current = (GiProbeSurface)0;
    if (anchor.Instance >= GiScene.InstanceCount) return false;
    uint view = anchor.Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    float3 local = InverseTransformInstancePosition(anchor.Position.xyz, GetPreviousInstanceTransform(anchor.Instance));
    float3 position = TransformInstanceLocalPosition(local, GetCurrentInstanceTransform(anchor.Instance));
    float4 clip = mul(float4(ClusterPositionForView(position, GiViews[view]), 1.0), GiViews[view].ViewProj);
    if (clip.w <= 0.0) return false;
    float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    if (any(uv < 0.0) || any(uv >= 1.0)) return false;
    current = ReadGiProbeSurface(uint3(uint2(uv * float2(GiSettings.Width, GiSettings.Height)), view));
    if (current.Position.w == 0.0 || current.Instance != anchor.Instance ||
        distance(current.Position.xyz, position) >= current.Normal.w * 2.0) return false;
    ClusterViewUniforms camera = GiViews[view];
    float3 previousPosition = mul(float4(ClusterPreviousPositionForView(anchor.Position.xyz, camera), 1.0), camera.PrevView).xyz;
    float3 previousNormal = mul(float4(anchor.Normal.xyz, 0.0), camera.PrevView).xyz;
    float3 currentPosition = mul(float4(ClusterPositionForView(position, camera), 1.0), camera.View).xyz;
    float3 currentNormal = mul(float4(current.Normal.xyz, 0.0), camera.View).xyz;
    if (dot(previousNormal, previousPosition) * dot(currentNormal, currentPosition) < 0.0) return false;
    current.Position.xyz = position;
    current.Seed = anchor.Seed;
    return true;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiProbeReprojection(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiProbeCount()) GiProbeReprojection[thread.x] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterGiProbeReprojection(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiProbeCount() || GiSettings.UseHistory == 0u || !GiLayoutChanged()) return;
    uint previous = (GiSettings.HistoryLayer ^ 1u) * GiProbeCount() + index;
    GiProbeSurface anchor = GiProbeSurfaces[previous];
    if (anchor.Position.w == 0.0 || GiProbeSamples[previous].x == 0.0) return;
    GiProbeSurface surface;
    if (!ReprojectGiProbeSurface(anchor, surface)) return;
    uint2 range = GiProbeTileRange(surface.Tile, GiSettings.HistoryLayer);
    uint bits = firstbithigh(GiProbeCount()) + 1u, indexMask = (1u << bits) - 1u;
    for (uint item = 0u; item < range.y; item++)
        if ((GiProbeReprojection[range.x + item] & indexMask) == index + 1u) return;
    float best = 0.0;
    uint selected = 0xffffffffu;
    for (uint item = 0u; item < range.y; item++)
    {
        if (item + 1u == range.y && GiCoveragePixel(surface.Tile) != 0xffffffffu) continue;
        uint at = range.x + item;
        if (GiProbeReprojection[at] != 0u) continue;
        GiProbeSurface candidate = GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + at];
        float2 separation = (float2(candidate.Pixel) - float2(surface.Pixel)) / 16.0;
        float maturity = min(GiProbeSamples[previous].x / 64.0, 1.0);
        float score = (0.5 + 0.5 * maturity) / (1.0 + dot(separation, separation));
        if (score > best) { best = score; selected = at; }
    }
    if (selected != 0xffffffffu)
    {
        uint rank = uint(saturate(best) * float((1u << (32u - bits)) - 1u));
        InterlockedMax(GiProbeReprojection[selected], (rank << bits) | (index + 1u));
    }
}

groupshared uint GiHistoryProbe;
groupshared float GiHistoryCount;
[shader("compute")]
[numthreads(64, 1, 1)]
void ReprojectGiScreenProbes(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = group.x, current = GiSettings.HistoryLayer * GiProbeCount() + index;
    if (lane == 0u)
    {
        GiHistoryProbe = 0xffffffffu; GiHistoryCount = 0.0;
        GiProbeSurface source = GiProbeSurfaces[current];
        if (GiSettings.UseHistory != 0u && source.Position.w > 0.0)
        {
            uint2 range = GiProbeTileRange(source.Tile, GiSettings.HistoryLayer);
            bool relocate = index == range.x + range.y - 1u && GiCoveragePixel(source.Tile) != 0xffffffffu;
            if (!relocate)
            {
                uint oldIndex = index;
                if (GiLayoutChanged())
                {
                    uint bits = firstbithigh(GiProbeCount()) + 1u;
                    uint address = GiProbeReprojection[index] & ((1u << bits) - 1u);
                    oldIndex = address == 0u ? 0xffffffffu : address - 1u;
                }
                if (oldIndex != 0xffffffffu)
                {
                    uint previous = (GiSettings.HistoryLayer ^ 1u) * GiProbeCount() + oldIndex;
                    GiProbeSurface anchor = GiProbeSurfaces[previous], projected;
                    if (anchor.Position.w > 0.0 && GiProbeSamples[previous].x > 0.0 &&
                        ReprojectGiProbeSurface(anchor, projected) && projected.Tile == source.Tile)
                    {
                        GiProbeSurfaces[current] = projected;
                        source = projected;
                        GiHistoryProbe = previous;
                        GiHistoryCount = min(GiProbeSamples[previous].x, GiSettings.RadianceChanged != 0u ? 8.0 : 64.0);
                    }
                }
            }
        }
        uint view = source.Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
        bool changing = GiSettings.SceneChanged != 0u || GiSettings.RadianceChanged != 0u || GiViewChanged(view);
        uint refresh = changing ? 2u : 8u;
        float threshold = changing ? (GiSettings.RadianceChanged != 0u ? 2.0 : 4.0) : 64.0;
        bool request = source.Position.w > 0.0 && (GiHistoryCount < threshold ||
            (changing && GiHistoryCount < 64.0 && (index * 13u + GiSettings.Frame) % refresh == 0u));
        GiProbeSamples[current] = float2(GiHistoryCount, request ? 1.0 : 0.0);
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane < 9u)
        GiProbeRadiance[current * 9u + lane] = GiHistoryProbe == 0xffffffffu ? 0.0 : GiProbeRadiance[GiHistoryProbe * 9u + lane];
}

groupshared uint GiRadianceNeighbors[36];
groupshared float GiRadianceAmounts[36];
groupshared float GiRadianceTotal, GiRadianceSamples, GiRadianceConfidence;
groupshared float3 GiRadianceMean;
[shader("compute")]
[numthreads(64, 1, 1)]
void FilterGiScreenProbes(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint index = group.x, current = GiSettings.HistoryLayer * GiProbeCount() + index;
    GiProbeSurface source = GiProbeSurfaces[current];
    if (source.Position.w == 0.0) return;
    uint view = source.Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    uint tile = source.Tile % (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
    int2 xy = int2(tile % GiSettings.ProbeWidth, tile / GiSettings.ProbeWidth);
    uint2 ownRange = GiProbeTileRange(source.Tile, GiSettings.HistoryLayer);
    float radius = source.Normal.w * 16.0 * 1.5 / sqrt(float(ownRange.y));
    if (lane < 36u)
    {
        GiRadianceAmounts[lane] = 0.0;
        uint offset = lane / 4u, item = lane % 4u;
        int2 neighbor = xy + int2(int(offset % 3u) - 1, int(offset / 3u) - 1);
        if (all(neighbor >= 0) && all(neighbor < int2(GiSettings.ProbeWidth, GiSettings.ProbeHeight)))
        {
            uint neighborTile = (view * GiSettings.ProbeHeight + uint(neighbor.y)) * GiSettings.ProbeWidth + uint(neighbor.x);
            uint2 range = GiProbeTileRange(neighborTile, GiSettings.HistoryLayer);
            if (item < range.y)
            {
                uint at = GiSettings.HistoryLayer * GiProbeCount() + range.x + item;
                GiProbeSurface anchor = GiProbeSurfaces[at];
                float count = GiProbeSamples[at].x;
                float3 delta = anchor.Position.xyz - source.Position.xyz;
                float square = dot(delta, delta), agreement = max(0.0, dot(source.Normal.xyz, anchor.Normal.xyz));
                if (anchor.Position.w > 0.0 && count > 0.0 && square <= radius * radius * 4.0 && agreement >= 0.95)
                {
                    float plane = max(0.0, 1.0 - abs(dot(delta, source.Normal.xyz)) / max(0.003, source.Normal.w * 1.5));
                    float weight = pow(agreement, 8.0) * plane / (1.0 + square / (radius * radius));
                    GiRadianceAmounts[lane] = weight * exp(-square / (radius * radius)) * count;
                    GiRadianceNeighbors[lane] = at;
                }
            }
        }
    }
    GroupMemoryBarrierWithGroupSync();
    if (lane == 0u)
    {
        GiRadianceTotal = 0.0;
        float variance = 0.0;
        for (uint item = 0u; item < 36u; item++)
        {
            float amount = GiRadianceAmounts[item]; GiRadianceTotal += amount;
            if (amount > 0.0) variance += amount * amount / GiProbeSamples[GiRadianceNeighbors[item]].x;
        }
        GiRadianceSamples = GiRadianceTotal * GiRadianceTotal / max(variance, 1e-20);
        GiRadianceConfidence = saturate((GiRadianceSamples - 1.0) / (GiRadianceSamples + 3.0));
    }
    GroupMemoryBarrierWithGroupSync();
    float3 coefficient = 0.0;
    if (lane < 9u)
    {
        for (uint item = 0u; item < 36u; item++)
            if (GiRadianceAmounts[item] > 0.0)
                coefficient += GiProbeRadiance[GiRadianceNeighbors[item] * 9u + lane].rgb * GiRadianceAmounts[item];
        coefficient /= max(GiRadianceTotal, 1e-20);
    }
    if (lane == 0u) GiRadianceMean = coefficient;
    GroupMemoryBarrierWithGroupSync();
    if (lane >= 9u) return;
    if (lane > 0u)
    {
        float3 low = lane < 4u ? GiRadianceMean * (0.8660254038 * source.Normal[lane % 3u]) : 0.0;
        coefficient = lerp(low, coefficient, GiRadianceConfidence);
    }
    GiFilteredProbeRadiance[current * 9u + lane] = float4(coefficient, lane == 0u ? GiRadianceSamples : 0.0);
}

#endif
