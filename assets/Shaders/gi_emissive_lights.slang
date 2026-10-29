#ifndef GI_EMISSIVE_LIGHTS_SLANG
#define GI_EMISSIVE_LIGHTS_SLANG

#include "gi_emissive_sampling.slang"
#include "gi_direct_sampling.slang"

RWStructuredBuffer<uint2> GiEmitterInstanceRanges;
RWStructuredBuffer<GiDirectLightProposal> GiDirectProposals;
RWStructuredBuffer<GiDirectLightGuide> GiDirectGuides;
RWStructuredBuffer<float4> GiDirectWeights;
RWStructuredBuffer<float4> GiDirectSurfaces;
RWStructuredBuffer<uint> GiEmissionDispatch;
RWTexture2DArray<float4> GiDirectRaw;
Texture2DArray<float4> GiDirectRawValues;
RWTexture2DArray<float4> GiDirectFiltered;
Texture2DArray<float4> GiDirectFilteredValues;
RWTexture2DArray<float4> GiDirectHistory;
RWTexture2DArray<float4> GiInitialDirectHistory;

uint2 GiDirectGrid() { return (uint2(GiEmissive.Width, GiEmissive.Height) + 7u) / 8u; }
uint GiDirectPixelCount() { return GiEmissive.Width * GiEmissive.Height * GiEmissive.ViewCount; }
uint GiDirectTileCount() { uint2 grid = GiDirectGrid(); return grid.x * grid.y * GiEmissive.ViewCount; }
uint3 GiDirectPixel(uint index)
{
    return uint3(index % GiEmissive.Width, index / GiEmissive.Width % GiEmissive.Height,
        index / (GiEmissive.Width * GiEmissive.Height));
}
bool GiDirectSurfacePresent(uint3 pixel)
{
    return GiDiffuse.Load(int4(pixel, 0)).w > 0.0 && !IsVisBufferEmpty(GiVisibility.Load(int4(pixel, 0)));
}
float4 GiDirectVariates(uint index)
{
    uint seed = index * 7919u + GiEmissive.Frame * 104729u + 911u;
    return float4(GiRandom(seed + 7103u), GiRandom(seed + 13007u), GiRandom(seed + 26017u), GiRandom(seed + 39023u));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeGiEmissiveHistory(uint3 thread : SV_DispatchThreadID)
{
    uint count = GiDirectPixelCount();
    if (thread.x >= count * 2u) return;
    uint3 pixel = GiDirectPixel(thread.x % count);
    pixel.z += thread.x / count * GiEmissive.ViewCount;
    GiInitialDirectHistory[pixel] = 0.0;
    GiDirectSurfaces[thread.x] = 0.0;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BeginGiEmissiveWork(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiEmissive.MaterialCount)
    {
        GiMaterialWorkCounts[thread.x] = 0u;
        GiMaterialWorkWrites[thread.x] = 0u;
        GiEmissionBounds[(GiEmissive.Frame & 1u) * GiEmissive.MaterialCount + thread.x] = 0.0;
    }
    if (thread.x == 0u) GiQueueCounts[0] = 0u;
    if (thread.x < 3u) GiEmissionDispatch[thread.x] = 1u;
}

bool GiInstanceEmits(uint instance)
{
    uint count = GiInstanceMaterialCount(instance);
    uint first = GetClusterMaterialSlotOffset(instance);
    for (uint index = 0u; index < count; index++)
        if (any(GiMaterialEmissionBound(first + index).rgb > 0.0)) return true;
    return false;
}

bool ReadGiEmittingTriangle(uint instance, uint pointers, uint index, out GiTriangleCandidate candidate)
{
    candidate = (GiTriangleCandidate)0;
    uint3 pointer = GiMeshFields.Load3(pointers + index * 12u);
    uint page;
    if (!GetGiTrianglePage(pointer, page)) return false;
    uint material = GetClusterMaterialSlotOffset(instance) + GetClusterLocalMaterial(PageHeap, page, pointer.y, pointer.z);
    if (!any(GiMaterialEmissionBound(material).rgb > 0.0)) return false;
    candidate.InstanceIndex = instance;
    candidate.Triangle = pointer;
    candidate.MaterialIndex = material;
    return true;
}

groupshared uint GiEmitterScan[64];
[shader("compute")]
[numthreads(64, 1, 1)]
void CountGiEmissiveTriangles(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint instance = group.x;
    if (instance >= GiEmissive.InstanceCount) return;
    uint address = GiInstanceField(instance);
    uint count = 0u;
    if (address != 0xffffffffu && GiInstanceEmits(instance))
    {
        uint triangles = GiMeshFields.Load(address + 124u);
        uint pointers = address + GiMeshFields.Load(address + 172u);
        for (uint index = lane; index < triangles; index += 64u)
        {
            GiTriangleCandidate candidate;
            if (ReadGiEmittingTriangle(instance, pointers, index, candidate)) count++;
        }
    }
    GiEmitterScan[lane] = count;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 32u; stride > 0u; stride /= 2u)
    {
        if (lane < stride) GiEmitterScan[lane] += GiEmitterScan[lane + stride];
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) GiEmitterInstanceRanges[instance] = uint2(0u, GiEmitterScan[0]);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGiEmitterInstanceRanges()
{
    uint first = 0u;
    for (uint instance = 0u; instance < GiEmissive.InstanceCount; instance++)
    {
        uint count = GiEmitterInstanceRanges[instance].y;
        GiEmitterInstanceRanges[instance] = uint2(first, count);
        first += count;
    }
    uint count = min(first, GiEmissive.EmitterCapacity);
    uint4 previous = GiEmitterState[0];
    GiEmitterState[1] = uint4(count != previous.x ? 1u : 0u, previous.x, previous.z, 0u);
    GiEmitterState[0] = uint4(count, (count + 63u) / 64u, 0u, GiEmissive.Frame);
    GiQueueCounts[0] = count;
    InterlockedAdd(GiQueueCounts[1], first - count);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CollectGiEmissiveTriangles(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint instance = group.x;
    if (instance >= GiEmissive.InstanceCount || GiEmitterInstanceRanges[instance].y == 0u) return;
    uint first = GiEmitterInstanceRanges[instance].x;
    uint address = GiInstanceField(instance);
    uint triangles = GiMeshFields.Load(address + 124u);
    uint pointers = address + GiMeshFields.Load(address + 172u);
    for (uint block = 0u; block < triangles; block += 64u)
    {
        GiTriangleCandidate candidate;
        uint present = block + lane < triangles && ReadGiEmittingTriangle(instance, pointers, block + lane, candidate) ? 1u : 0u;
        GiEmitterScan[lane] = present;
        GroupMemoryBarrierWithGroupSync();
        for (uint stride = 1u; stride < 64u; stride *= 2u)
        {
            uint add = lane >= stride ? GiEmitterScan[lane - stride] : 0u;
            GroupMemoryBarrierWithGroupSync();
            GiEmitterScan[lane] += add;
            GroupMemoryBarrierWithGroupSync();
        }
        uint at = first + GiEmitterScan[lane] - present;
        if (present != 0u && at < GiEmissive.EmitterCapacity)
        {
            GiCandidates[at] = candidate;
            InterlockedAdd(GiMaterialWorkCounts[candidate.MaterialIndex], 1u);
        }
        first += GiEmitterScan[63];
        GroupMemoryBarrierWithGroupSync();
    }
}

float3 GiEmitterTranslation()
{
    return GiEmitterState[0].w == GiEmissive.Frame ? 0.0 : GiViews[0].PreviousDataTranslation;
}

float GiEmitterMass(GiEmissiveTriangle light, float3 position, float3 normal)
{
    float3 center = light.PositionArea.xyz + (light.EdgeUFlux.xyz + light.EdgeV.xyz) / 3.0;
    float3 delta = center + GiEmitterTranslation() - position;
    float square = dot(delta, delta);
    float3 direction = delta * rsqrt(max(square, 1e-8));
    float3 plane = normalize(cross(light.EdgeUFlux.xyz, light.EdgeV.xyz));
    return light.EdgeUFlux.w * max(0.0, dot(normal, direction)) * abs(dot(plane, direction)) /
        max(square, light.SamplePositionRadius.w * light.SamplePositionRadius.w);
}

float GiEmitterVisibility(float3 position, float3 normal, float3 direction, float distance)
{
    GiGlobalField field = GiGlobalFields[0];
    GiDistanceHit hit = TraceGiGlobalScene(field, GiGlobalDistances, position + normal * field.Pitch,
        direction, field.Pitch * 0.1, max(field.Pitch * 0.1, distance - field.Pitch * 2.0));
    return hit.Hit == 0u ? 1.0 : 0.0;
}

// The sparse training paths share the emitting-surface distribution and its material-evaluated radiance.
float3 SampleGiEmissiveIrradiance(float3 position, float3 normal, uint seed)
{
    GiDirectLightProposal proposal = SelectGiEmitter(GiRandom(seed));
    if (proposal.Probability == 0.0) return 0.0;
    GiEmissiveTriangle light = GiEmissiveTriangles[proposal.Emitter];
    float3 point = light.SamplePositionRadius.xyz + GiEmitterTranslation();
    float3 delta = point - position;
    float square = dot(delta, delta), distance = sqrt(square);
    float3 direction = delta / distance;
    float3 plane = normalize(cross(light.EdgeUFlux.xyz, light.EdgeV.xyz));
    float cosine = max(0.0, dot(normal, direction)) * abs(dot(plane, direction));
    if (cosine == 0.0) return 0.0;
    return EvaluateGiEmissiveIrradiance(light.Emission.rgb, 1.0, normal, plane, delta,
        light.PositionArea.w / proposal.Probability) * GiEmitterVisibility(position, normal, direction, distance);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SampleGiDirectProposals(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    uint count = GiDirectTileCount();
    if (index >= count * 2u) return;
    uint tile = index / 2u;
    GiDirectLightProposal proposal = SelectGiEmitter(GiDirectVariates(index).x);
    GiDirectLightGuide guide = GiDirectGuides[((GiEmissive.Frame + 1u) & 1u) * count + tile];
    uint4 state = GiEmitterState[0];
    bool useGuide = guide.Brightness > 0.0 && guide.Emitter < state.x &&
        all(GiEmissiveTriangles[guide.Emitter].TriangleInstance == guide.TriangleInstance);
    if (proposal.Probability > 0.0 && useGuide)
    {
        if ((index & 1u) != 0u)
        {
            proposal.Emitter = guide.Emitter;
            proposal.Probability = GiEmissiveTriangles[guide.Emitter].EdgeUFlux.w / asfloat(state.z);
        }
        proposal.Probability = proposal.Probability * 0.5 + (proposal.Emitter == guide.Emitter ? 0.5 : 0.0);
    }
    GiDirectProposals[index] = proposal;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CreateGiDirectSamples(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiDirectPixelCount()) return;
    GiDirectWeights[index] = 0.0;
    uint3 pixel = GiDirectPixel(index);
    if (!GiDirectSurfacePresent(pixel)) return;
    float3 position = GiWorldPosition(pixel);
    float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy);
    uint2 grid = GiDirectGrid();
    int2 low = int2(floor((float2(pixel.xy) + 0.5) / 8.0 - 0.5));
    uint emitters[8];
    float weights[8];
    uint count = 0u;
    float total = 0.0;
    for (uint corner = 0u; corner < 4u; corner++)
    {
        int2 tile = low + int2(corner & 1u, corner >> 1u);
        if (any(tile < 0) || any(tile >= int2(grid))) continue;
        uint first = ((pixel.z * grid.y + uint(tile.y)) * grid.x + uint(tile.x)) * 2u;
        for (uint candidate = 0u; candidate < 2u; candidate++)
        {
            GiDirectLightProposal proposal = GiDirectProposals[first + candidate];
            if (proposal.Probability == 0.0) continue;
            float weight = GiEmitterMass(GiEmissiveTriangles[proposal.Emitter], position, normal) / proposal.Probability;
            emitters[count] = proposal.Emitter;
            weights[count] = weight;
            total += weight;
            count++;
        }
    }
    if (total <= 0.0) return;
    float4 variates = GiDirectVariates(index + GiDirectTileCount() * 2u);
    float choice = variates.x * total;
    uint selected = count - 1u;
    for (uint candidate = 0u; candidate < count; candidate++)
    {
        choice -= weights[candidate];
        if (choice < 0.0) { selected = candidate; break; }
    }
    uint emitter = emitters[selected];
    GiEmissiveTriangle light = GiEmissiveTriangles[emitter];
    float mass = GiEmitterMass(light, position, normal);
    float3 barycentric = GiEmitterBarycentric(variates.yz);
    float3 point = light.PositionArea.xyz + light.EdgeUFlux.xyz * barycentric.y + light.EdgeV.xyz * barycentric.z;
    float3 delta = point - position;
    float distance = length(delta);
    GiTraceRay ray;
    ray.Origin = position;
    ray.Direction = delta / distance;
    ray.Start = 0.0;
    ray.Limit = distance;
    GiRays[index] = ray;
    GiTriangleCandidate candidate = (GiTriangleCandidate)0;
    candidate.RayIndex = index;
    candidate.InstanceIndex = light.TriangleInstance.w;
    candidate.Triangle = light.TriangleInstance.xyz;
    candidate.MaterialIndex = light.Material.x;
    candidate.Limit = distance;
    GiCandidates[index] = candidate;
    GiCandidateHits[index] = float4(distance, barycentric);
    GiDirectWeights[index] = float4(light.PositionArea.w * total / (float(count) * mass), asfloat(emitter), 0.0, 0.0);
    InterlockedAdd(GiMaterialWorkCounts[candidate.MaterialIndex], 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterGiDirectSamples(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiDirectPixelCount() || GiDirectWeights[index].x == 0.0) return;
    uint material = GiCandidates[index].MaterialIndex, offset;
    InterlockedAdd(GiMaterialWorkWrites[material], 1u, offset);
    GiMaterialWorkIndices[GiMaterialWorkRanges[material].x + offset] = index;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResolveGiDirectLight(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiDirectPixelCount()) return;
    uint3 pixel = GiDirectPixel(index);
    float4 weight = GiDirectWeights[index];
    float3 value = 0.0;
    float changing = GiEmitterState[1].x != 0u ? 1.0 : 0.0;
    if (weight.x > 0.0)
    {
        GiTraceRay ray = GiRays[index];
        GiSurfaceSample sample = GiSurfaceSamples[index];
        GiEmissiveTriangle emitter = GiEmissiveTriangles[asuint(weight.y)];
        changing = max(changing, float(emitter.Material.y));
        float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy);
        float3 delta = sample.Position - ray.Origin;
        float distance = length(delta);
        float3 direction = delta / distance;
        value = EvaluateGiEmissiveIrradiance(sample.Emission, sample.Coverage, normal,
            sample.GeometricNormal, delta, weight.x);
        if (any(value > 0.0)) value *= GiEmitterVisibility(ray.Origin, GiPlaneNormal(pixel), direction, distance);
    }
    GiDirectRaw[pixel] = float4(value, changing);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void UpdateGiDirectGuides(uint3 thread : SV_DispatchThreadID)
{
    uint tile = thread.x;
    if (tile >= GiDirectTileCount()) return;
    uint2 grid = GiDirectGrid();
    uint view = tile / (grid.x * grid.y);
    uint2 low = uint2(tile % grid.x, tile / grid.x % grid.y) * 8u;
    GiDirectLightGuide guide = (GiDirectLightGuide)0;
    for (uint sample = 0u; sample < 64u; sample++)
    {
        uint2 xy = low + uint2(sample % 8u, sample / 8u);
        if (any(xy >= uint2(GiEmissive.Width, GiEmissive.Height))) continue;
        uint index = (view * GiEmissive.Height + xy.y) * GiEmissive.Width + xy.x;
        float4 weight = GiDirectWeights[index];
        if (weight.x == 0.0) continue;
        float brightness = GiEmissionBrightness(GiDirectRawValues.Load(int4(xy, view, 0)).rgb);
        if (brightness > guide.Brightness)
        {
            guide.Emitter = asuint(weight.y);
            guide.Brightness = brightness;
            guide.TriangleInstance = GiEmissiveTriangles[guide.Emitter].TriangleInstance;
        }
    }
    GiDirectGuides[(GiEmissive.Frame & 1u) * GiDirectTileCount() + tile] = guide;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void FilterGiDirectLight(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiDirectPixelCount()) return;
    uint3 pixel = GiDirectPixel(index);
    float3 value = 0.0;
    float total = 0.0, changing = 0.0;
    if (GiDirectSurfacePresent(pixel))
    {
        float3 position = GiWorldPosition(pixel), plane = GiPlaneNormal(pixel);
        float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy);
        float footprint = GiPixelFootprint(position, pixel.z);
        for (uint neighbor = 0u; neighbor < 9u; neighbor++)
        {
            int2 offset = int2(neighbor % 3u, neighbor / 3u) - 1;
            int2 xy = int2(pixel.xy) + offset;
            if (any(xy < 0) || any(xy >= int2(GiEmissive.Width, GiEmissive.Height))) continue;
            uint3 other = uint3(uint2(xy), pixel.z);
            if (!GiDirectSurfacePresent(other)) continue;
            float agreement = dot(normal, DecodeGiNormal(GiNormal.Load(int4(other, 0)).xy));
            float separation = abs(dot(plane, GiWorldPosition(other) - position)) / (footprint * 1.5);
            if (agreement < 0.95 || separation > 1.0) continue;
            float weight = (offset.x == 0 ? 1.0 : 0.5) * (offset.y == 0 ? 1.0 : 0.5) *
                pow(agreement, 8.0) * exp(-4.0 * separation * separation);
            float4 light = GiDirectRawValues.Load(int4(other, 0));
            value += light.rgb * weight;
            total += weight;
            changing = max(changing, light.w);
        }
    }
    GiDirectFiltered[pixel] = float4(total > 0.0 ? value / total : 0.0, changing);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void AccumulateGiDirectLight(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x, count = GiDirectPixelCount();
    if (index >= count) return;
    uint3 pixel = GiDirectPixel(index);
    uint layer = GiEmissive.Frame & 1u;
    uint3 output = uint3(pixel.xy, layer * GiEmissive.ViewCount + pixel.z);
    if (!GiDirectSurfacePresent(pixel))
    {
        GiDirectHistory[output] = 0.0;
        GiDirectSurfaces[layer * count + index] = 0.0;
        return;
    }
    float3 position = GiWorldPosition(pixel), normal = GiPlaneNormal(pixel);
    float footprint = GiPixelFootprint(position, pixel.z);
    float2 uv = (float2(pixel.xy) + 0.5) / float2(GiEmissive.Width, GiEmissive.Height) - GiMotion.Load(int4(pixel, 0));
    float3 oldLight = 0.0;
    float oldCount = 0.0, total = 0.0;
    if (GiEmissive.UseHistory != 0u && all(uv >= 0.0) && all(uv < 1.0))
    {
        float2 coordinate = uv * float2(GiEmissive.Width, GiEmissive.Height) - 0.5;
        int2 first = int2(floor(coordinate));
        float2 fraction = frac(coordinate);
        for (uint corner = 0u; corner < 4u; corner++)
        {
            int2 offset = int2(corner & 1u, corner >> 1u);
            int2 xy = first + offset;
            if (any(xy < 0) || any(xy >= int2(GiEmissive.Width, GiEmissive.Height))) continue;
            uint previous = (pixel.z * GiEmissive.Height + uint(xy.y)) * GiEmissive.Width + uint(xy.x);
            float4 surface = GiDirectSurfaces[(layer ^ 1u) * count + previous];
            float4 light = GiDirectHistory[uint3(uint2(xy), (layer ^ 1u) * GiEmissive.ViewCount + pixel.z)];
            float3 delta = surface.xyz + GiViews[pixel.z].PreviousDataTranslation - position;
            if (light.w == 0.0 || dot(normal, UnpackGiDirectNormal(asuint(surface.w))) < 0.95 ||
                abs(dot(delta, normal)) > max(0.003, footprint * 1.5) || length(delta) > footprint * 3.0) continue;
            float2 amount = select(offset != 0, fraction, 1.0 - fraction);
            float weight = amount.x * amount.y;
            oldLight += light.rgb * weight;
            oldCount += light.w * weight;
            total += weight;
        }
    }
    float4 current = GiDirectFilteredValues.Load(int4(pixel, 0));
    if (total > 0.0) { oldLight /= total; oldCount /= total; }
    oldCount = min(oldCount, current.w > 0.0 ? 3.0 : 31.0);
    GiDirectHistory[output] = float4((oldLight * oldCount + current.rgb) / (oldCount + 1.0), oldCount + 1.0);
    GiDirectSurfaces[layer * count + index] = float4(position, asfloat(PackGiDirectNormal(normal)));
}

#endif
