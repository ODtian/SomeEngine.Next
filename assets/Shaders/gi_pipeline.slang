#ifndef GI_PIPELINE_SLANG
#define GI_PIPELINE_SLANG
#include "gi_scene_tracing.slang"
#include "gi_material_queries.slang"
#include "gi_radiance_cache.slang"
#include "cluster_common.slang"
#include "cluster_page_requests.slang"
#include "gi_geometry_pages.slang"
#include "render_lighting.slang"
#include "brdf.slang"
#include "gi_emissive_sampling.slang"

struct GiSceneParameters
{
    uint InstanceCount;
    uint MaterialCount;
    uint CandidateCapacity;
    uint TrainingCount;
    uint GridSide;
    uint TrainingStage;
    uint MeshFieldsIndexCount;
    uint UseInstanceGroups;
    float NearDistance;
    float TraceDistance;
    uint FieldsChanged;
    uint NearObjectWordCount;
};
ConstantBuffer<GiSceneParameters> GiScene;
ByteAddressBuffer GiMeshFields;
StructuredBuffer<uint> GiMeshFieldsIndex;
ByteAddressBuffer PageHeap;
StructuredBuffer<float4> GiInstanceBounds;
StructuredBuffer<float4> GiInstanceGroups;
StructuredBuffer<uint> GiInstanceOrder;
RWStructuredBuffer<uint> GiNearObjectWords;
RWStructuredBuffer<uint> GiGlobalDistanceWrites;
RWStructuredBuffer<uint> GiStaticDistances;
RWStructuredBuffer<uint> GiInstanceMovement;
RWStructuredBuffer<uint> GiGlobalFieldChanges;
StructuredBuffer<float> GiGlobalDistances;
RWStructuredBuffer<GiGlobalField> GiGlobalFields;
RWStructuredBuffer<uint> GiQueueCounts;
RWStructuredBuffer<uint> GiMaterialWorkCounts;
RWStructuredBuffer<uint> GiMaterialWorkWrites;
RWStructuredBuffer<uint> GiMaterialDispatch;
RWStructuredBuffer<uint> GiClosestCandidates;
RWStructuredBuffer<float4> GiScreenHits;
RWStructuredBuffer<float4> GiProbeIncoming;
RWStructuredBuffer<GiSurfaceSample> GiTrainingSurfaces;
RWStructuredBuffer<uint> GiTrainingProbes;
RWStructuredBuffer<uint> GiTrainingWork;
RWStructuredBuffer<uint> GiTrainingDispatch;
RWStructuredBuffer<uint> GiQueryDispatch;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<ClusteredLightGridParameters> LightingViews;

float3 GiCachePosition(float3 position)
{
    ClusterViewUniforms view = GiViews[0];
    return position + view.WorldTranslation + view.WorldOriginHigh + view.WorldOriginLow;
}

uint GiInstanceField(uint instance)
{
    uint root = GetClusterInstanceBvhRoot(instance);
    return root < GiScene.MeshFieldsIndexCount ? GiMeshFieldsIndex[root] : 0xffffffffu;
}

float GiMeshDistance(uint address, float3 point)
{
    GiMeshDistanceGrid grid = GiMeshFields.Load<GiMeshDistanceGrid>(address);
    uint data = address + GiMeshFields.Load(address + 160u);
    float3 delta = point - float3(grid.CenterX, grid.CenterY, grid.CenterZ);
    float3 local = float3(dot(delta, grid.AxisX), dot(delta, grid.AxisY), dot(delta, grid.AxisZ));
    float3 coordinate = clamp((local - grid.Lower) / grid.CellSize - 0.5, 0.0.xxx, float3(grid.Shape - 1u));
    uint3 first = uint3(floor(coordinate));
    float3 amount = frac(coordinate);
    float value = 0.0;
    for (uint corner = 0u; corner < 8u; corner++)
    {
        uint3 offset = uint3(corner & 1u, (corner >> 1u) & 1u, corner >> 2u);
        uint3 cell = min(first + offset, grid.Shape - 1u);
        float3 weight = select(offset != 0u, amount, 1.0 - amount);
        uint index = grid.FirstDistance + cell.x + cell.y * grid.Shape.x + cell.z * grid.Shape.x * grid.Shape.y;
        value += asfloat(GiMeshFields.Load(data + index * 4u)) * weight.x * weight.y * weight.z;
    }
    float3 sampled = grid.Lower + (coordinate + 0.5) * grid.CellSize;
    return value + length(local - sampled);
}

groupshared float3 GiBoundsLow[64];
groupshared float3 GiBoundsHigh[64];
[shader("compute")]
[numthreads(1, 1, 1)]
void BeginGiGlobalUpdate()
{
    GiGlobalFieldChanges[0] = GiScene.FieldsChanged;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void UpdateGiInstanceMovement(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.InstanceCount) return;
    RenderInstanceTransform current = GetCurrentInstanceTransform(index), previous = GetPreviousInstanceTransform(index);
    bool moving = any(current.Position != previous.Position + GiViews[0].PreviousDataTranslation) ||
        any(current.Rotation != previous.Rotation) || current.Scale != previous.Scale || any(current.Stretch != previous.Stretch);
    uint old = GiInstanceMovement[index];
    GiInstanceMovement[index] = (old & 1u) | (moving ? 3u : 0u);
    if (moving && (old & 1u) == 0u) InterlockedOr(GiGlobalFieldChanges[0], 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildGiGlobalBounds(uint lane : SV_GroupIndex)
{
    float3 low = 1e20.xxx, high = -1e20.xxx;
    for (uint index = lane; index < GiScene.InstanceCount; index += 64u)
    {
        if (GiInstanceField(index) == 0xffffffffu) continue;
        low = min(low, GiInstanceBounds[index * 4u].xyz);
        high = max(high, GiInstanceBounds[index * 4u + 1u].xyz);
    }
    GiBoundsLow[lane] = low;
    GiBoundsHigh[lane] = high;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 32u; stride > 0u; stride /= 2u)
    {
        if (lane < stride)
        {
            GiBoundsLow[lane] = min(GiBoundsLow[lane], GiBoundsLow[lane + stride]);
            GiBoundsHigh[lane] = max(GiBoundsHigh[lane], GiBoundsHigh[lane + stride]);
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0u)
    {
        low = GiBoundsLow[0]; high = GiBoundsHigh[0];
        if (GiScene.InstanceCount == 0u || any(low > high)) { low = -1.0; high = 1.0; }
        GiGlobalField old = GiGlobalFields[0];
        float3 translation = GiViews[0].PreviousDataTranslation;
        if (GiScene.FieldsChanged == 0u && old.Pitch > 0.0 &&
            all(low >= old.Lower + translation + old.Pitch) && all(high <= old.Upper + translation - old.Pitch))
        {
            old.Lower += translation; old.Upper += translation;
            GiGlobalFields[0] = old;
            return;
        }
        low -= GiScene.NearDistance; high += GiScene.NearDistance;
        float3 size = high - low;
        float pitch = max(0.02, max(size.x, max(size.y, size.z)) / float(GiScene.GridSide - 4u));
        float3 center = (low + high) * 0.5;
        GiGlobalField field = (GiGlobalField)0;
        field.Shape = GiScene.GridSide.xxx;
        field.Pitch = pitch;
        field.Lower = center - float3(field.Shape) * pitch * 0.5;
        field.Upper = field.Lower + float3(field.Shape) * pitch;
        field.Error = pitch * 0.01;
        GiGlobalFields[0] = field;
        GiGlobalFieldChanges[0] = 1u;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiStaticDistances(uint3 thread : SV_DispatchThreadID)
{
    uint count = GiScene.GridSide * GiScene.GridSide * GiScene.GridSide;
    if (thread.x < count && GiGlobalFieldChanges[0] != 0u)
        GiStaticDistances[thread.x] = asuint(min(GiScene.TraceDistance, GiGlobalFields[0].Pitch * 4.0));
}

void ComposeGiInstanceDistances(uint instance, uint lane, bool moving)
{
    if (instance >= GiScene.InstanceCount) return;
    if ((GiInstanceMovement[instance] != 0u) != moving || (!moving && GiGlobalFieldChanges[0] == 0u)) return;
    uint address = GiInstanceField(instance);
    if (address == 0xffffffffu) return;
    GiGlobalField field = GiGlobalFields[0];
    float band = field.Pitch * 4.0;
    uint3 low = uint3(clamp(floor((GiInstanceBounds[instance * 4u].xyz - band - field.Lower) / field.Pitch), 0.0.xxx, float3(field.Shape - 1u)));
    uint3 high = uint3(clamp(floor((GiInstanceBounds[instance * 4u + 1u].xyz + band - field.Lower) / field.Pitch), 0.0.xxx, float3(field.Shape - 1u)));
    uint3 shape = high - low + 1u;
    RenderInstanceTransform transform = GetCurrentInstanceTransform(instance);
    float3 scale = abs(transform.Scale * transform.Stretch);
    float distanceScale = min(scale.x, min(scale.y, scale.z));
    for (uint index = lane; index < shape.x * shape.y * shape.z; index += 64u)
    {
        uint3 cell = low + uint3(index % shape.x, index / shape.x % shape.y, index / (shape.x * shape.y));
        float3 position = field.Lower + (float3(cell) + 0.5) * field.Pitch;
        float distance = GiMeshDistance(address, InverseTransformInstancePosition(position, transform)) * distanceScale;
        uint ignored;
        uint at = cell.x + cell.y * field.Shape.x + cell.z * field.Shape.x * field.Shape.y;
        if (moving) InterlockedMin(GiGlobalDistanceWrites[at], asuint(distance), ignored);
        else InterlockedMin(GiStaticDistances[at], asuint(distance), ignored);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ComposeGiStaticDistances(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    ComposeGiInstanceDistances(group.x, lane, false);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CopyGiStaticDistances(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiScene.GridSide * GiScene.GridSide * GiScene.GridSide)
        GiGlobalDistanceWrites[thread.x] = GiStaticDistances[thread.x];
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ComposeGiGlobalDistances(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    ComposeGiInstanceDistances(group.x, lane, true);
}

void AddGiTriangleCandidates(uint rayIndex, uint instance, GiTraceRay ray, float limit)
{
    uint address = GiInstanceField(instance);
    if (address == 0xffffffffu) return;
    float2 box = GiRayBox(GiInstanceBounds[instance * 4u].xyz, GiInstanceBounds[instance * 4u + 1u].xyz,
        ray.Origin, ray.Direction);
    if (box.y < max(ray.Start, box.x) || box.x >= limit) return;
    RenderInstanceTransform transform = GetCurrentInstanceTransform(instance);
    float3 origin = InverseTransformInstancePosition(ray.Origin, transform);
    float3 direction = InverseTransformInstanceVector(ray.Direction, transform);
    float3 scale = abs(transform.Scale * transform.Stretch);
    float expansion = max(0.0, GetInstanceBoundsExpansion(instance)) / min(scale.x, min(scale.y, scale.z));
    GiMeshSurfaceTrace trace = BeginGiMeshSurfaceTrace(GiMeshFields, address, origin, direction, ray.Start, limit, expansion);
    uint4 offsets = GiMeshFields.Load4(address + 160u) + address;
    uint2 materials = GiMeshFields.Load2(address + 176u);
    uint firstMaterial = GetClusterMaterialSlotOffset(instance);
    uint2 neighborhood;
    while (NextGiMeshSurface(GiMeshFields, address, trace, neighborhood))
    {
        for (uint index = 0u; index < neighborhood.y; index++)
        {
            uint triangle = GiMeshFields.Load(offsets.z + (neighborhood.x + index) * 4u);
            uint localMaterial = materials.y != 0u ? GiMeshFields.Load(address + materials.x + triangle * 4u) : 0u;
            uint at;
            InterlockedAdd(GiQueueCounts[0], 1u, at);
            if (at >= GiScene.CandidateCapacity) { InterlockedAdd(GiQueueCounts[1], 1u); continue; }
            GiTriangleCandidate candidate = (GiTriangleCandidate)0;
            candidate.RayIndex = rayIndex;
            candidate.InstanceIndex = instance;
            candidate.MaterialIndex = firstMaterial + localMaterial;
            candidate.Triangle = GiMeshFields.Load3(offsets.w + triangle * 12u);
            candidate.Start = ray.Start;
            candidate.Limit = limit;
            GiCandidates[at] = candidate;
            InterlockedAdd(GiMaterialWorkCounts[candidate.MaterialIndex], 1u);
        }
    }
}

groupshared float3 GiNearTileLower;
groupshared float3 GiNearTileUpper;
[shader("compute")]
[numthreads(64, 1, 1)]
void BuildGiNearObjectWords(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint tileHeight = GiSettings.ProbeHeight;
    uint view = group.x / (GiSettings.ProbeWidth * tileHeight);
    uint tile = group.x % (GiSettings.ProbeWidth * tileHeight);
    if (lane == 0u)
    {
        float3 low = 1e20.xxx, high = -1e20.xxx;
        uint2 range = GiProbeTileRange(group.x, GiSettings.HistoryLayer);
        for (uint offset = 0u; offset < range.y; offset++)
        {
            GiTraceRay ray = GiRays[range.x + offset];
            if (ray.Limit <= ray.Start) continue;
            low = min(low, ray.Origin); high = max(high, ray.Origin);
        }
        GiNearTileLower = low - GiScene.NearDistance;
        GiNearTileUpper = high + GiScene.NearDistance;
    }
    GroupMemoryBarrierWithGroupSync();
    for (uint word = lane; word < GiScene.NearObjectWordCount; word += 64u)
    {
        uint mask = 0u;
        for (uint bit = 0u; bit < 32u; bit++)
        {
            uint instance = word * 32u + bit;
            if (instance >= GiScene.InstanceCount) break;
            if (all(GiInstanceBounds[instance * 4u].xyz <= GiNearTileUpper) &&
                all(GiInstanceBounds[instance * 4u + 1u].xyz >= GiNearTileLower)) mask |= 1u << bit;
        }
        GiNearObjectWords[group.x * GiScene.NearObjectWordCount + word] = mask;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BeginGiTriangleQueries(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index < GiScene.MaterialCount) { GiMaterialWorkCounts[index] = 0u; GiMaterialWorkWrites[index] = 0u; }
    if (index < GiProbeCount() + GiScene.TrainingCount)
    {
        GiClosestDistances[index] = asuint(GiScene.TraceDistance);
        GiClosestCandidates[index] = 0xffffffffu;
    }
    if (index == 0u) GiQueueCounts[0] = 0u;
    if (index == 1u && GiScene.TrainingStage == 0u) GiQueueCounts[1] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CollectGiTriangleCandidates(uint3 thread : SV_DispatchThreadID)
{
    uint rayIndex = thread.x + (GiScene.TrainingStage != 0u ? GiProbeCount() : 0u);
    if (rayIndex >= GiProbeCount() + GiScene.TrainingCount) return;
    GiTraceRay ray = GiRays[rayIndex];
    if (ray.Limit <= ray.Start) return;
    bool training = rayIndex >= GiProbeCount();
    float limit = min(GiScene.NearDistance, GiScreenHits[rayIndex].w);
    if (!training)
    {
        uint tile = GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + rayIndex].Tile;
        for (uint word = 0u; word < GiScene.NearObjectWordCount; word++)
        {
            uint mask = GiNearObjectWords[tile * GiScene.NearObjectWordCount + word];
            while (mask != 0u)
            {
                uint bit = firstbitlow(mask); mask &= mask - 1u;
                AddGiTriangleCandidates(rayIndex, word * 32u + bit, ray, limit);
            }
        }
        return;
    }
    uint groupCount = GiScene.UseInstanceGroups != 0u ? (GiScene.InstanceCount + 63u) / 64u : 1u;
    for (uint group = 0u; group < groupCount; group++)
    {
        uint first = 0u, end = GiScene.InstanceCount;
        if (GiScene.UseInstanceGroups != 0u)
        {
            float2 box = GiRayBox(GiInstanceGroups[group * 2u].xyz, GiInstanceGroups[group * 2u + 1u].xyz, ray.Origin, ray.Direction);
            if (box.y < max(ray.Start, box.x) || box.x >= limit) continue;
            first = group * 64u; end = min(first + 64u, GiScene.InstanceCount);
        }
        for (uint index = first; index < end; index++)
        {
            uint instance = GiScene.UseInstanceGroups != 0u ? GiInstanceOrder[index] : index;
            AddGiTriangleCandidates(rayIndex, instance, ray, limit);
        }
    }
}

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGiQueryDispatch()
{
    GiQueryDispatch[0] = (min(GiQueueCounts[0], GiScene.CandidateCapacity) + 63u) / 64u;
    GiQueryDispatch[1] = 1u; GiQueryDispatch[2] = 1u;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGiMaterialWorkRanges()
{
    uint first = 0u;
    for (uint material = 0u; material < GiScene.MaterialCount; material++)
    {
        uint count = GiMaterialWorkCounts[material];
        GiMaterialWorkRanges[material] = uint2(first, count);
        GiMaterialWorkWrites[material] = 0u;
        GiMaterialDispatch[material * 3u] = (count + 63u) / 64u;
        GiMaterialDispatch[material * 3u + 1u] = 1u;
        GiMaterialDispatch[material * 3u + 2u] = 1u;
        first += count;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterGiTriangleCandidates(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= min(GiQueueCounts[0], GiScene.CandidateCapacity)) return;
    uint material = GiCandidates[index].MaterialIndex, offset;
    InterlockedAdd(GiMaterialWorkWrites[material], 1u, offset);
    GiMaterialWorkIndices[GiMaterialWorkRanges[material].x + offset] = index;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ReduceGiCandidateDistances(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= min(GiQueueCounts[0], GiScene.CandidateCapacity)) return;
    float distance = GiCandidateHits[index].x;
    if (distance < GiCandidates[index].Limit)
        InterlockedMin(GiClosestDistances[GiCandidates[index].RayIndex], asuint(distance));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SelectGiClosestCandidates(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= min(GiQueueCounts[0], GiScene.CandidateCapacity)) return;
    GiTriangleCandidate candidate = GiCandidates[index];
    float distance = GiCandidateHits[index].x;
    if (distance < candidate.Limit && asuint(distance) == GiClosestDistances[candidate.RayIndex])
        InterlockedMin(GiClosestCandidates[candidate.RayIndex], index);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiMaterialWork(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiScene.MaterialCount) GiMaterialWorkCounts[thread.x] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CountGiSurfaceWork(uint3 thread : SV_DispatchThreadID)
{
    uint ray = thread.x;
    if (ray >= GiProbeCount() + GiScene.TrainingCount) return;
    uint candidate = GiClosestCandidates[ray];
    if (candidate != 0xffffffffu) InterlockedAdd(GiMaterialWorkCounts[GiCandidates[candidate].MaterialIndex], 1u);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ScatterGiSurfaceWork(uint3 thread : SV_DispatchThreadID)
{
    uint ray = thread.x;
    if (ray >= GiProbeCount() + GiScene.TrainingCount) return;
    uint candidate = GiClosestCandidates[ray];
    if (candidate == 0xffffffffu) return;
    uint material = GiCandidates[candidate].MaterialIndex, offset;
    InterlockedAdd(GiMaterialWorkWrites[material], 1u, offset);
    GiMaterialWorkIndices[GiMaterialWorkRanges[material].x + offset] = candidate;
}

float3 GiDiffuseDirect(GiSurfaceSample surface, bool includeEmission = true)
{
    GiGlobalField field = GiGlobalFields[0];
    float3 value = DiffuseEnvironmentIrradiance(surface.Occlusion);
    ClusteredLightGridParameters lighting = LightingViews[0];
    for (uint index = 0u; index < lighting.DirectionalCount + lighting.NonDirectionalCount; index++)
    {
        GPULight light = LightBuffer[index];
        float3 direction;
        float distance = length(field.Upper - field.Lower), attenuation = 1.0;
        if (index < lighting.DirectionalCount) direction = -normalize(light.Direction);
        else
        {
            float3 delta = light.Position - surface.Position;
            distance = length(delta);
            if (distance >= light.Range) continue;
            direction = delta / distance;
            attenuation = EvaluateDistanceAttenuation(distance * distance, light.Range);
            if (light.OuterConeCos > 0.0)
                attenuation *= EvaluateSpotCone(direction, light.Direction, light.InnerConeCos, light.OuterConeCos);
        }
        float cosine = max(0.0, dot(surface.Normal, direction));
        if (cosine == 0.0) continue;
        GiDistanceHit blocker = TraceGiGlobalScene(field, GiGlobalDistances,
            surface.Position + surface.GeometricNormal * field.Pitch, direction,
            field.Pitch, max(field.Pitch, distance - field.Pitch));
        if (blocker.Hit == 0u) value += light.Color * (light.Intensity * cosine * attenuation / GI_PI);
    }
    if (includeEmission) value += SampleGiEmissiveIrradiance(surface.Position, surface.Normal,
        GiMixBits(asuint(surface.Position.x) ^ asuint(surface.Position.y) ^ GiSettings.Frame));
    return surface.Diffuse * (surface.Coverage * value);
}

float3 GiSkyRadiance(float3 direction)
{
    return lerp(float3(0.03, 0.035, 0.04), float3(0.17, 0.23, 0.34), saturate(direction.y * 0.5 + 0.5));
}

GiSurfaceSample ReadGiScreenSurface(uint3 pixel)
{
    GiSurfaceSample result = (GiSurfaceSample)0;
    result.Position = GiWorldPosition(pixel);
    float4 normals = GiNormal.Load(int4(pixel, 0)), diffuse = GiDiffuse.Load(int4(pixel, 0));
    result.Normal = DecodeGiNormal(normals.xy); result.GeometricNormal = DecodeGiNormal(normals.zw);
    result.Diffuse = diffuse.rgb; result.Occlusion = diffuse.w; result.Coverage = 1.0;
    return result;
}

float4 ResolveGiRayRadiance(uint index)
{
    GiTraceRay ray = GiRays[index];
    if (ray.Limit <= ray.Start) return 0.0;
    uint candidate = GiClosestCandidates[index];
    if (candidate != 0xffffffffu)
    {
        GiSurfaceSample surface = GiSurfaceSamples[candidate];
        float4 cached = QueryGiRadianceValue(GiCachePosition(surface.Position), surface.GeometricNormal, GiCache.CellSize);
        return float4(cached.w > 0.0 ? cached.rgb : GiDiffuseDirect(surface), surface.Distance);
    }
    if (GiScreenHits[index].w < ray.Limit)
    {
        uint3 pixel = uint3(GiScreenHits[index].xyz);
        GiSurfaceSample surface = ReadGiScreenSurface(pixel);
        float4 cached = QueryGiRadianceValue(GiCachePosition(surface.Position), surface.GeometricNormal, GiCache.CellSize);
        return float4(cached.w > 0.0 ? cached.rgb : ReadGiScreenReflectedRadiance(pixel), GiScreenHits[index].w);
    }
    GiDistanceHit hit = TraceGiGlobalScene(GiGlobalFields[0], GiGlobalDistances, ray.Origin, ray.Direction,
        GiScene.NearDistance, ray.Limit);
    if (hit.Hit != 0u)
        return float4(QueryGiReflectedRadiance(GiCachePosition(hit.Position), hit.Normal, GiGlobalFields[0].Pitch), hit.Distance);
    return float4(GiSkyRadiance(ray.Direction), 0.0);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResolveGiProbeRays(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < GiProbeCount()) GiProbeIncoming[thread.x] = ResolveGiRayRadiance(thread.x);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiRadianceTraining(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index == 0u) GiTrainingWork[0] = 0u;
    if (index >= GiScene.TrainingCount) return;
    GiTrainingProbes[index] = 0xffffffffu;
    GiTrainingSurfaces[index] = (GiSurfaceSample)0;
    GiRadianceTraining[index] = (GiRadianceSample)0;
    GiRays[GiProbeCount() + index] = (GiTraceRay)0;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void SelectGiTrainingProbes(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount || GiSettings.Frame % 4u != 0u) return;
    uint first = (index * GiProbeCount() + GiScene.TrainingCount - 1u) / GiScene.TrainingCount;
    uint end = ((index + 1u) * GiProbeCount() + GiScene.TrainingCount - 1u) / GiScene.TrainingCount;
    uint width = end - first, probe = first + (GiSettings.Frame / 4u + index) % width;
    bool trainReceiver = ((index + GiSettings.Frame / 4u) & 1u) == 0u;
    for (uint offset = 0u; offset < width; offset++)
    {
        uint current = GiSettings.HistoryLayer * GiProbeCount() + probe;
        GiProbeSurface anchor = GiProbeSurfaces[current];
        if (anchor.Position.w > 0.0 && GiProbeSamples[current].y > 0.0)
        {
            GiSurfaceSample parent = (GiSurfaceSample)0;
            uint candidate = GiClosestCandidates[probe];
            if (candidate != 0xffffffffu) parent = GiSurfaceSamples[candidate];
            else if (GiScreenHits[probe].w < GiScene.TraceDistance)
                parent = ReadGiScreenSurface(uint3(GiScreenHits[probe].xyz));
            if (!trainReceiver && parent.Coverage > 0.0 && any(parent.Diffuse > 0.0))
            {
                GiTrainingSurfaces[index] = parent;
                GiTrainingProbes[index] = probe;
                InterlockedAdd(GiTrainingWork[0], 1u);
                return;
            }
            uint view = anchor.Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
            uint3 pixel = uint3(anchor.Pixel, view);
            GiSurfaceSample receiver = ReadGiScreenSurface(pixel);
            if (trainReceiver && any(receiver.Diffuse > 0.0))
            {
                float cosine = max(0.0, dot(receiver.Normal, GiRays[probe].Direction));
                GiRadianceSample sample = (GiRadianceSample)0;
                sample.Position = GiCachePosition(anchor.Position.xyz); sample.Normal = anchor.Normal.xyz;
                sample.Radiance = ReadGiScreenDirectRadiance(pixel) + receiver.Diffuse * (GiProbeIncoming[probe].rgb * (2.0 * cosine));
                sample.Valid = 1u; sample.Weight = 1.0;
                GiRadianceTraining[index] = sample;
                GiTrainingProbes[index] = probe;
                InterlockedAdd(GiTrainingWork[0], 1u);
                return;
            }
        }
        probe++; if (probe == end) probe = first;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ContinueGiTrainingRays(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount) return;
    uint rayIndex = GiProbeCount() + index;
    GiSurfaceSample parent = GiTrainingSurfaces[index];
    GiTraceRay ray = (GiTraceRay)0;
    GiScreenHits[rayIndex] = float4(0.0, 0.0, 0.0, GiScene.TraceDistance);
    if (parent.Coverage > 0.0)
    {
        uint seed = index * 289u + GiSettings.Frame * 7919u + 919u;
        float z = sqrt(GiRandom(seed)), angle = GiRandom(seed + 7u) * 2.0 * GI_PI;
        float3 normal = parent.Normal;
        float3 tangent = normalize(cross(normal, abs(normal.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0)));
        ray.Origin = parent.Position + parent.GeometricNormal * 0.012;
        ray.Direction = tangent * (sqrt(1.0 - z * z) * cos(angle)) + cross(normal, tangent) * (sqrt(1.0 - z * z) * sin(angle)) + normal * z;
        ray.Start = 0.002; ray.Limit = GiScene.TraceDistance;
        if (dot(parent.GeometricNormal, ray.Direction) <= 0.0)
        {
            ray.Limit = 0.0;
            GiRays[rayIndex] = ray;
            return;
        }
        uint probe = GiTrainingProbes[index];
        uint view = GiProbeSurfaces[GiSettings.HistoryLayer * GiProbeCount() + probe].Tile / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
        uint3 pixel;
        float distance;
        bool screen = TraceGiScreen(ray.Origin, ray.Direction, view, pixel, distance);
        GiScreenHits[rayIndex] = float4(float3(pixel), screen ? distance : GiScene.TraceDistance);
    }
    GiRays[rayIndex] = ray;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void FinishGiRadianceTraining(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount) return;
    GiSurfaceSample parent = GiTrainingSurfaces[index];
    if (parent.Coverage == 0.0) return;
    float3 incoming = ResolveGiRayRadiance(GiProbeCount() + index).rgb;
    GiRadianceSample sample = (GiRadianceSample)0;
    sample.Position = GiCachePosition(parent.Position); sample.Normal = parent.GeometricNormal;
    sample.Radiance = GiDiffuseDirect(parent) + parent.Diffuse * (parent.Coverage * incoming);
    sample.Valid = 1u; sample.Weight = 1.0;
    GiRadianceTraining[index] = sample;
}
[shader("compute")]
[numthreads(1, 1, 1)]
void PrepareGiRadianceTraining()
{
    bool available = GiTrainingWork[0] > 0u;
    if (available && (GiCache.CacheMode & 2u) != 0u) GiNeuralUpdates[0]++;
    uint groups[5] = { (GiScene.TrainingCount + 63u) / 64u, (GiCache.HashCount + 63u) / 64u,
        (GiScene.TrainingCount + 31u) / 32u, (899u * 8u + 63u) / 64u, (899u + 63u) / 64u };
    for (uint item = 0u; item < 5u; item++)
    {
        GiTrainingDispatch[item * 3u] = available ? groups[item] : 0u;
        GiTrainingDispatch[item * 3u + 1u] = 1u; GiTrainingDispatch[item * 3u + 2u] = 1u;
    }
}

#endif
