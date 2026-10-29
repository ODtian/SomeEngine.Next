#ifndef GI_PIPELINE_SLANG
#define GI_PIPELINE_SLANG
#include "gi_scene_tracing.slang"
#include "gi_material_queries.slang"
#include "gi_radiance_cache.slang"
#include "cluster_common.slang"
#include "cluster_page_requests.slang"
#include "render_lighting.slang"
#include "brdf.slang"

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
    float2 Padding0;
};
ConstantBuffer<GiSceneParameters> GiScene;
ByteAddressBuffer GiMeshFields;
StructuredBuffer<uint2> GiMeshFieldsIndex;
ByteAddressBuffer GiHierarchy;
ByteAddressBuffer PageHeap;
StructuredBuffer<float4> GiInstanceBounds;
StructuredBuffer<float4> GiInstanceGroups;
StructuredBuffer<uint> GiInstanceOrder;
RWByteAddressBuffer GiPageRequests;
RWStructuredBuffer<uint> GiGlobalDistanceWrites;
StructuredBuffer<float> GiGlobalDistances;
RWStructuredBuffer<GiGlobalField> GiGlobalFields;
RWStructuredBuffer<uint> GiQueueCounts;
RWStructuredBuffer<uint> GiMaterialWorkCounts;
RWStructuredBuffer<uint> GiMaterialWorkWrites;
RWStructuredBuffer<uint> GiMaterialDispatch;
RWStructuredBuffer<uint> GiClosestDistances;
RWStructuredBuffer<uint> GiClosestCandidates;
RWStructuredBuffer<float4> GiScreenHits;
RWStructuredBuffer<float4> GiProbeIncoming;
RWStructuredBuffer<GiSurfaceSample> GiTrainingSurfaces;
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
    return root < GiScene.MeshFieldsIndexCount ? GiMeshFieldsIndex[root].x : 0xffffffffu;
}

uint GiInstanceMaterialCount(uint instance)
{
    uint root = GetClusterInstanceBvhRoot(instance);
    return root < GiScene.MeshFieldsIndexCount ? GiMeshFieldsIndex[root].y : 0u;
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
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearGiGlobalDistances(uint3 thread : SV_DispatchThreadID)
{
    uint count = GiScene.GridSide * GiScene.GridSide * GiScene.GridSide;
    if (thread.x < count) GiGlobalDistanceWrites[thread.x] = asuint(min(GiScene.TraceDistance, GiGlobalFields[0].Pitch * 4.0));
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ComposeGiGlobalDistances(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint instance = group.x;
    if (instance >= GiScene.InstanceCount) return;
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
        InterlockedMin(GiGlobalDistanceWrites[cell.x + cell.y * field.Shape.x + cell.z * field.Shape.x * field.Shape.y], asuint(distance), ignored);
    }
}

bool GetGiTrianglePage(uint3 pointer, out uint page)
{
    page = 0u;
    uint node = pointer.x / 240u;
    uint child = (pointer.x % 240u - 140u) / 16u;
    ClusterHierarchySlice slice = LoadClusterNode(GiHierarchy, node, child);
    uint capacity = GiViews[0].MaxPageFaults;
    for (uint pageIndex = ClusterSliceFirstPage(slice); pageIndex < ClusterSliceFirstPage(slice) + ClusterSlicePageCount(slice); pageIndex++)
    {
        uint previous;
        GiPageRequests.InterlockedMax(ClusterPageRequestPriorityOffset(capacity, pageIndex),
        PackClusterStreamingPriority(1.0, 1.0, 2u), previous);
        if (previous == 0u)
        {
        uint demand;
        GiPageRequests.InterlockedAdd(ClusterPageDemandCountOffset(capacity), 1u, demand);
        if (demand < capacity) GiPageRequests.Store2(ClusterPageDemandOffset(capacity, demand), uint2(pageIndex, 0u));
        if (!ClusterSliceAvailable(slice))
        {
            uint at;
            GiPageRequests.InterlockedAdd(0u, 1u, at);
            if (at < capacity) GiPageRequests.Store4(4u + at * 16u, uint4(node * 4u + child, pageIndex, 1u, 0u));
        }
        }
    }
    if (!ClusterSliceAvailable(slice)) return false;
    page = ClusterSlicePageOffset(slice);
    return true;
}

struct GiTriangleCollector : IGiMeshTriangles
{
    uint RayIndex;
    uint Instance;
    bool intersect(uint3 pointer, float3 origin, float3 direction, float start, float limit,
        out float distance, out float3 barycentric)
    {
        distance = limit; barycentric = 0.0;
        uint page; if (!GetGiTrianglePage(pointer, page)) return false;
        uint material = GetClusterMaterialSlotOffset(Instance) + GetClusterLocalMaterial(PageHeap, page, pointer.y, pointer.z);
        uint at;
        InterlockedAdd(GiQueueCounts[0], 1u, at);
        if (at >= GiScene.CandidateCapacity) { InterlockedAdd(GiQueueCounts[1], 1u); return false; }
        GiTriangleCandidate candidate;
        candidate.RayIndex = RayIndex;
        candidate.InstanceIndex = Instance;
        candidate.Triangle = pointer;
        candidate.MaterialIndex = material;
        candidate.Start = start;
        candidate.Limit = limit;
        GiCandidates[at] = candidate;
        InterlockedAdd(GiMaterialWorkCounts[material], 1u);
        return false;
    }
};

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
    float limit = training ? ray.Limit : min(GiScene.NearDistance, GiScreenHits[rayIndex].w);
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
            uint address = GiInstanceField(instance);
            if (address == 0xffffffffu) continue;
            float2 box = GiRayBox(GiInstanceBounds[instance * 4u].xyz, GiInstanceBounds[instance * 4u + 1u].xyz, ray.Origin, ray.Direction);
            if (box.y < max(ray.Start, box.x) || box.x >= limit) continue;
            RenderInstanceTransform transform = GetCurrentInstanceTransform(instance);
            GiTriangleCollector collector;
            collector.RayIndex = rayIndex; collector.Instance = instance;
            float3 localOrigin = InverseTransformInstancePosition(ray.Origin, transform);
            float3 localDirection = InverseTransformInstanceVector(ray.Direction, transform);
            if (training) TraceGiMeshCells(GiMeshFields, address, collector, localOrigin, localDirection, ray.Start, limit);
            else TraceGiMeshDistanceField(GiMeshFields, address, collector, localOrigin, localDirection, ray.Start, limit);
        }
    }
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

float3 GiDiffuseDirect(GiSurfaceSample surface)
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
            float fade = saturate(1.0 - pow(distance / light.Range, 4.0));
            attenuation = fade * fade / max(distance * distance, 1e-4);
            if (light.OuterConeCos > 0.0)
                attenuation *= saturate((dot(-direction, light.Direction) - light.OuterConeCos) /
                    max(light.InnerConeCos - light.OuterConeCos, 1e-4));
        }
        float cosine = max(0.0, dot(surface.Normal, direction));
        if (cosine == 0.0) continue;
        GiDistanceHit blocker = TraceGiGlobalScene(field, GiGlobalDistances,
            surface.Position + surface.GeometricNormal * field.Pitch, direction,
            field.Pitch, max(field.Pitch, distance - field.Pitch));
        if (blocker.Hit == 0u) value += light.Color * (light.Intensity * cosine * attenuation / GI_PI);
    }
    value += SampleGiEmissiveIrradiance(surface.Position, surface.Normal,
        GiMixBits(asuint(surface.Position.x) ^ asuint(surface.Position.y) ^ GiSettings.Frame));
    return surface.Diffuse * (surface.Coverage * value);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResolveGiProbeRays(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiProbeCount()) return;
    GiTraceRay ray = GiRays[index];
    float3 radiance = 0.0;
    uint candidate = GiClosestCandidates[index];
    if (candidate != 0xffffffffu)
    {
        GiSurfaceSample surface = GiSurfaceSamples[candidate];
        float4 cached = QueryGiRadianceValue(GiCachePosition(surface.Position), surface.GeometricNormal, GiCache.CellSize);
        radiance = cached.w > 0.0 ? cached.rgb : GiDiffuseDirect(surface);
    }
    else if (GiScreenHits[index].w < GiScene.TraceDistance)
        radiance = GiReflectedRadiance.Load(int4(uint3(GiScreenHits[index].xyz), 0)).rgb;
    else if (ray.Limit > ray.Start)
    {
        GiDistanceHit hit = TraceGiGlobalScene(GiGlobalFields[0], GiGlobalDistances, ray.Origin, ray.Direction,
            GiScene.NearDistance, ray.Limit);
        if (hit.Hit != 0u)
            radiance = QueryGiReflectedRadiance(GiCachePosition(hit.Position), hit.Normal, GiGlobalFields[0].Pitch);
    }
    GiProbeIncoming[index] = float4(radiance, 0.0);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void CreateGiTrainingRays(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount) return;
    uint probe = (index * GiProbeCount() / GiScene.TrainingCount + GiSettings.Frame) % GiProbeCount();
    GiRays[GiProbeCount() + index] = GiRays[probe];
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ContinueGiTrainingRays(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount) return;
    uint rayIndex = GiProbeCount() + index;
    uint candidate = GiClosestCandidates[rayIndex];
    GiSurfaceSample surface = (GiSurfaceSample)0;
    GiTraceRay ray = (GiTraceRay)0;
    if (candidate != 0xffffffffu)
    {
        surface = GiSurfaceSamples[candidate];
        float z = sqrt(GiRandom(index * 7919u + GiSettings.Frame * 104729u + 1u));
        float angle = GiRandom(index * 12007u + GiSettings.Frame * 7411u + 7u) * 2.0 * GI_PI;
        float3 n = surface.Normal;
        float3 tangent = normalize(cross(n, abs(n.y) > 0.95 ? float3(1, 0, 0) : float3(0, 1, 0)));
        ray.Origin = surface.Position + surface.GeometricNormal * 0.012;
        ray.Direction = tangent * (sqrt(1.0 - z * z) * cos(angle)) + cross(n, tangent) * (sqrt(1.0 - z * z) * sin(angle)) + n * z;
        ray.Start = 0.002;
        ray.Limit = GiScene.TraceDistance;
    }
    GiTrainingSurfaces[index] = surface;
    GiRays[rayIndex] = ray;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void FinishGiRadianceTraining(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiScene.TrainingCount) return;
    GiSurfaceSample parent = GiTrainingSurfaces[index];
    GiRadianceSample sample = (GiRadianceSample)0;
    if (parent.Coverage > 0.0)
    {
        float3 incoming = 0.0;
        uint candidate = GiClosestCandidates[GiProbeCount() + index];
        if (candidate != 0xffffffffu)
        {
            GiSurfaceSample surface = GiSurfaceSamples[candidate];
            float4 cached = QueryGiRadianceValue(GiCachePosition(surface.Position), surface.GeometricNormal, GiCache.CellSize);
            incoming = cached.w > 0.0 ? cached.rgb : GiDiffuseDirect(surface);
        }
        sample.Position = GiCachePosition(parent.Position);
        sample.Normal = parent.GeometricNormal;
        sample.Radiance = GiDiffuseDirect(parent) + parent.Diffuse * (parent.Coverage * incoming);
        sample.Valid = 1u;
        sample.Weight = 1.0;
    }
    GiRadianceTraining[index] = sample;
    uint probe = (index * GiProbeCount() / GiScene.TrainingCount + GiSettings.Frame) % GiProbeCount();
    uint current = GiSettings.HistoryLayer * GiProbeCount() + probe;
    GiProbeSurface anchor = GiProbeSurfaces[current];
    sample = (GiRadianceSample)0;
    if (anchor.Position.w > 0.0)
    {
        uint view = probe / (GiSettings.ProbeWidth * GiSettings.ProbeHeight);
        float4 clip = mul(float4(ClusterPositionForView(anchor.Position.xyz, GiViews[view]), 1.0), GiViews[view].ViewProj);
        float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
        uint2 pixel = uint2(clamp(uv * float2(GiSettings.Width, GiSettings.Height), 0.0.xx, float2(GiSettings.Width - 1u, GiSettings.Height - 1u)));
        sample.Position = GiCachePosition(anchor.Position.xyz);
        sample.Normal = anchor.Normal.xyz;
        sample.Radiance = GiReflectedRadiance.Load(int4(pixel, view, 0u)).rgb;
        sample.Valid = 1u;
        sample.Weight = 1.0;
    }
    GiRadianceTraining[GiScene.TrainingCount + index] = sample;
}

#endif
