#ifndef GI_EMISSIVE_LIGHTS_SLANG
#define GI_EMISSIVE_LIGHTS_SLANG

#include "gi_emissive_sampling.slang"
#include "gi_normal.slang"

RWStructuredBuffer<GiDirectLightProposal> GiDirectProposals;
RWStructuredBuffer<GiDirectLightGuide> GiDirectGuides;
RWStructuredBuffer<float4> GiDirectWeights;
RWStructuredBuffer<uint> GiEmissionDispatch;
RWTexture2DArray<float4> GiDirectRaw;
Texture2DArray<float4> GiDirectRawValues;
RWTexture2DArray<float4> GiDirectFiltered;
RWTexture2DArray<float4> GiDirectMovingFiltered;
Texture2DArray<float4> GiDirectFilteredValues;
Texture2DArray<float4> GiDirectMovingFilteredValues;
RWTexture2DArray<float4> GiDirectHistory;
RWTexture2DArray<float4> GiMovingDirectHistory;
RWTexture2DArray<float4> GiInitialDirectHistory;
RWTexture2DArray<float4> GiInitialMovingDirectHistory;

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
    return GiSurfaceInstances.Load(int4(pixel, 0)) != 0u && !IsVisBufferEmpty(GiVisibility.Load(int4(pixel, 0)));
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
    GiInitialMovingDirectHistory[pixel] = 0.0;
    if (thread.x < GiDirectTileCount() * 2u) GiDirectGuides[thread.x] = (GiDirectLightGuide)0;
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
    if (thread.x == 0u)
    {
        GiQueueCounts[0] = 0u;
        GiEmitterChanges[0] = GiScene.FieldsChanged != 0u || GiEmitterChanges[1] != 0u ? 3u : GiEmissive.MaterialsChanged != 0u ? 2u : 0u;
        GiEmitterChanges[1] = 0u;
    }
    if (thread.x < GiEmissive.InstanceCount) GiEmitterInstanceChanges[thread.x] = 0u;
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
    if (!GetGiTrianglePage(pointer, GiViews[0].MaxPageFaults, page))
    {
        InterlockedOr(GiEmitterChanges[1], 1u);
        return false;
    }
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
    if ((GiEmitterChanges[0] & 1u) == 0u) return;
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
    uint4 previous = GiEmitterState[0];
    GiEmitterState[1] = uint4(GiEmitterChanges[0] != 0u ? 1u : 0u, previous.x, previous.z, 0u);
    if ((GiEmitterChanges[0] & 1u) == 0u)
    {
        GiQueueCounts[0] = previous.x;
        GiEmitterState[0].w = GiEmissive.Frame;
        return;
    }
    uint first = 0u, firstNode = 0u, total = 0u;
    for (uint instance = 0u; instance < GiEmissive.InstanceCount; instance++)
    {
        uint requested = GiEmitterInstanceRanges[instance].y;
        total += requested;
        uint count = min(requested, GiEmissive.EmitterCapacity - min(first, GiEmissive.EmitterCapacity));
        GiEmitterInstanceRanges[instance] = uint2(first, count);
        uint leaves = count == 0u ? 0u : GiEmitterLeafCount(count);
        GiEmitterTreeRanges[instance] = uint2(firstNode, leaves);
        firstNode += leaves * 2u;
        first += count;
    }
    uint count = min(first, GiEmissive.EmitterCapacity);
    GiEmitterState[1] = uint4(1u, previous.x, previous.z, 0u);
    GiEmitterState[0] = uint4(count, (count + 63u) / 64u, 0u, GiEmissive.Frame);
    GiQueueCounts[0] = count;
    InterlockedAdd(GiQueueCounts[1], total - count);
}

bool GiEmitterNeedsUpdate(uint instance, uint material)
{
    RenderInstanceTransform current = GetCurrentInstanceTransform(instance), previous = GetPreviousInstanceTransform(instance);
    return (GiEmitterChanges[0] & 1u) != 0u || GiEmissive.MaterialsChanged != 0u ||
        GiMaterialEmissionBound(material).w > 0.0 || any(current.Position != previous.Position) ||
        any(current.Rotation != previous.Rotation) || current.Scale != previous.Scale || any(current.Stretch != previous.Stretch);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CollectGiEmissiveTriangles(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint instance = group.x;
    if (instance >= GiEmissive.InstanceCount || GiEmitterInstanceRanges[instance].y == 0u) return;
    uint first = GiEmitterInstanceRanges[instance].x;
    if ((GiEmitterChanges[0] & 1u) == 0u)
    {
        uint count = GiEmitterInstanceRanges[instance].y;
        for (uint item = lane; item < count; item += 64u)
        {
            uint at = first + item;
            GiEmissiveTriangle light = GiEmissiveTriangles[at];
            if (!GiEmitterNeedsUpdate(instance, light.Material.x)) continue;
            uint page;
            if (!GetGiTrianglePage(light.TriangleInstance.xyz, GiViews[0].MaxPageFaults, page))
            {
                InterlockedOr(GiEmitterChanges[1], 1u);
                GiEmissiveTriangles[at].EdgeUFlux.w = 0.0;
                InterlockedOr(GiEmitterState[1].x, 1u);
                InterlockedOr(GiEmitterInstanceChanges[instance], 1u);
                continue;
            }
            GiTriangleCandidate candidate = (GiTriangleCandidate)0;
            candidate.InstanceIndex = instance; candidate.Triangle = light.TriangleInstance.xyz;
            candidate.MaterialIndex = light.Material.x;
            GiCandidates[at] = candidate;
            InterlockedAdd(GiMaterialWorkCounts[candidate.MaterialIndex], 1u);
            InterlockedOr(GiEmitterState[1].x, 1u);
            InterlockedOr(GiEmitterInstanceChanges[instance], 1u);
        }
        return;
    }
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

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterGiEmissiveCandidates(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiEmitterState[0].x) return;
    if ((GiEmitterChanges[0] & 1u) == 0u)
    {
        GiEmissiveTriangle light = GiEmissiveTriangles[index];
        if (!GiEmitterNeedsUpdate(light.TriangleInstance.w, light.Material.x)) return;
    }
    uint material = GiCandidates[index].MaterialIndex, offset;
    InterlockedAdd(GiMaterialWorkWrites[material], 1u, offset);
    GiMaterialWorkIndices[GiMaterialWorkRanges[material].x + offset] = index;
}

float3 GiEmitterTranslation()
{
    return GiEmitterState[0].w == GiEmissive.Frame ? 0.0 : GiViews[0].PreviousDataTranslation;
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
    GiDirectLightProposal proposal = SelectGiEmittingObject(position, normal, GiRandom(seed + 7103u));
    if (proposal.Probability == 0.0) return 0.0;
    GiEmitterPointSample sample = SampleGiEmitterTriangle(position, normal, proposal.Instance,
        float3(GiRandom(seed + 13007u), GiRandom(seed + 26017u), GiRandom(seed + 39023u)));
    GiEmissiveTriangle light = GiEmissiveTriangles[sample.Emitter];
    float3 point = light.PositionArea.xyz + light.EdgeUFlux.xyz * sample.Barycentric.y + light.EdgeV.xyz * sample.Barycentric.z;
    float3 delta = point - position;
    float distance = length(delta);
    float3 plane = normalize(cross(light.EdgeUFlux.xyz, light.EdgeV.xyz));
    return EvaluateGiEmissiveIrradiance(light.Emission.rgb, 1.0, normal, plane, delta,
        1.0 / (proposal.Probability * sample.AreaPdf)) * GiEmitterVisibility(position, normal, delta / distance, distance);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SampleGiDirectProposals(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    uint count = GiDirectTileCount();
    if (index >= count * 2u) return;
    uint tile = index / 2u;
    uint2 grid = GiDirectGrid();
    uint view = tile / (grid.x * grid.y);
    uint2 corner = uint2(tile % grid.x, tile / grid.x % grid.y) * 8u;
    uint3 pixel = uint3(min(corner + 4u, uint2(GiEmissive.Width, GiEmissive.Height) - 1u), view);
    if (!GiDirectSurfacePresent(pixel))
    {
        for (uint item = 0u; item < 64u; item++)
        {
            uint2 candidate = corner + uint2(item % 8u, item / 8u);
            if (any(candidate >= uint2(GiEmissive.Width, GiEmissive.Height))) continue;
            pixel.xy = candidate;
            if (GiDirectSurfacePresent(pixel)) break;
        }
    }
    GiDirectProposals[index] = (GiDirectLightProposal)0;
    if (!GiDirectSurfacePresent(pixel)) return;
    float3 normal = GiPlaneNormal(pixel), position = GiWorldPosition(pixel) + normal * 0.012;
    GiDirectLightProposal proposal = SelectGiEmittingObject(position, normal, GiDirectVariates(index).x);
    GiDirectLightGuide guide = GiDirectGuides[((GiEmissive.Frame + 1u) & 1u) * count + tile];
    bool useGuide = GiEmissive.UseHistory != 0u && guide.Brightness > 0.0 &&
        guide.Instance < GiEmissive.InstanceCount && GiEmitterInstanceRanges[guide.Instance].y > 0u;
    if (proposal.Probability > 0.0 && useGuide)
    {
        if ((index & 1u) != 0u)
        {
            proposal.Instance = guide.Instance;
            proposal.Probability = GiEmittingObjectProbability(position, normal, guide.Instance);
        }
        proposal.Probability = proposal.Probability * 0.5 + (proposal.Instance == guide.Instance ? 0.5 : 0.0);
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
    float3 normal = GiPlaneNormal(pixel), position = GiWorldPosition(pixel) + normal * 0.012;
    uint2 grid = GiDirectGrid();
    int2 low = int2(floor((float2(pixel.xy) + 0.5) / 8.0 - 0.5));
    uint instances[8];
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
            float weight = GiEmittingObjectMass(position, normal, proposal.Instance) / proposal.Probability;
            instances[count] = proposal.Instance; weights[count] = weight;
            total += weight; count++;
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
    uint instance = instances[selected];
    float mass = GiEmittingObjectMass(position, normal, instance);
    GiEmitterPointSample sample = SampleGiEmitterTriangle(position, normal, instance, variates.yzw);
    GiEmissiveTriangle light = GiEmissiveTriangles[sample.Emitter];
    float3 point = light.PositionArea.xyz + light.EdgeUFlux.xyz * sample.Barycentric.y + light.EdgeV.xyz * sample.Barycentric.z;
    float3 delta = point - position;
    float distance = length(delta);
    GiTraceRay ray;
    ray.Origin = position; ray.Direction = delta / distance; ray.Start = 0.0; ray.Limit = distance;
    GiRays[index] = ray;
    GiTriangleCandidate candidate = (GiTriangleCandidate)0;
    candidate.RayIndex = index; candidate.InstanceIndex = light.TriangleInstance.w;
    candidate.Triangle = light.TriangleInstance.xyz; candidate.MaterialIndex = light.Material.x; candidate.Limit = distance;
    GiCandidates[index] = candidate;
    GiCandidateHits[index] = float4(distance, sample.Barycentric);
    GiDirectWeights[index] = float4(total / (float(count) * mass * sample.AreaPdf), asfloat(sample.Emitter), 0.0, 0.0);
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
    if (weight.x > 0.0)
    {
        GiTraceRay ray = GiRays[index];
        GiSurfaceSample sample = GiSurfaceSamples[index];
        GiEmissiveTriangle emitter = GiEmissiveTriangles[asuint(weight.y)];
        float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy);
        float3 delta = sample.Position - ray.Origin;
        float distance = length(delta);
        float3 direction = delta / distance;
        value = EvaluateGiEmissiveIrradiance(sample.Emission, sample.Coverage, normal,
            sample.GeometricNormal, delta, weight.x);
        if (any(value > 0.0)) value *= GiEmitterVisibility(ray.Origin, GiPlaneNormal(pixel), direction, distance);
    }
    GiDirectRaw[pixel] = float4(value, 0.0);
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
            guide.Instance = GiEmissiveTriangles[asuint(weight.y)].TriangleInstance.w;
            guide.Brightness = brightness;
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
    float3 fixedLight = 0.0, movingLight = 0.0;
    float total = 0.0;
    if (GiDirectSurfacePresent(pixel))
    {
        float3 position = GiWorldPosition(pixel), plane = GiPlaneNormal(pixel);
        float3 normal = DecodeGiNormal(GiNormal.Load(int4(pixel, 0)).xy);
        float footprint = GiPixelFootprint(position, pixel.z);
        for (uint neighbor = 0u; neighbor < 9u; neighbor++)
        {
            int2 offset = int2(neighbor % 3u, neighbor / 3u) - 1, xy = int2(pixel.xy) + offset;
            if (any(xy < 0) || any(xy >= int2(GiEmissive.Width, GiEmissive.Height))) continue;
            uint3 other = uint3(uint2(xy), pixel.z);
            if (!GiDirectSurfacePresent(other)) continue;
            float agreement = dot(normal, DecodeGiNormal(GiNormal.Load(int4(other, 0)).xy));
            float separation = abs(dot(plane, GiWorldPosition(other) - position)) / (footprint * 1.5);
            if (agreement < 0.95 || separation > 1.0) continue;
            float weight = (offset.x == 0 ? 1.0 : 0.5) * (offset.y == 0 ? 1.0 : 0.5) *
                pow(agreement, 8.0) * exp(-4.0 * separation * separation);
            float4 light = GiDirectRawValues.Load(int4(other, 0));
            uint neighborIndex = (other.z * GiEmissive.Height + other.y) * GiEmissive.Width + other.x;
            float4 proposal = GiDirectWeights[neighborIndex];
            uint instance = proposal.x > 0.0 ? GiEmissiveTriangles[asuint(proposal.y)].TriangleInstance.w : 0u;
            bool moving = proposal.x > 0.0 && (GiInstanceMovement[instance] & 1u) != 0u;
            if (moving) movingLight += light.rgb * weight;
            else fixedLight += light.rgb * weight;
            total += weight;
        }
    }
    GiDirectFiltered[pixel] = float4(fixedLight / max(total, 1e-20), 0.0);
    GiDirectMovingFiltered[pixel] = float4(movingLight / max(total, 1e-20), 0.0);
}

#endif
