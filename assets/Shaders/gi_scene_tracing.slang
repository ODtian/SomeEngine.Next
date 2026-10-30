#ifndef GI_SCENE_TRACING_SLANG
#define GI_SCENE_TRACING_SLANG

#include "gi_mesh_distance_field.slang"

interface IGiSceneMeshes
{
    GiMeshHit traceDistanceField(uint instance, float3 origin, float3 direction, float start, float limit);
};

struct GiSceneHit
{
    GiMeshHit Mesh;
    uint Instance;
    uint Stage;
    uint InstanceQueries;
    uint BoundsTests;
};

struct GiGlobalField
{
    float3 Lower;
    float Pitch;
    float3 Upper;
    float Error;
    uint3 Shape;
    uint Padding0;
    uint3 Padding1;
    uint Padding2;
};

struct GiDistanceHit
{
    float3 Position;
    float Distance;
    float3 Normal;
    uint Hit;
};

GiSceneHit MissGiScene(float limit)
{
    GiSceneHit hit = (GiSceneHit)0;
    hit.Mesh = MissGiMesh(limit);
    hit.Instance = 0xffffffffu;
    return hit;
}

void MergeGiMeshHit(inout GiSceneHit scene, GiMeshHit mesh, uint instance)
{
    uint tests = scene.Mesh.TriangleTests + mesh.TriangleTests;
    uint samples = scene.Mesh.FieldSamples + mesh.FieldSamples;
    uint visits = scene.Mesh.CellVisits + mesh.CellVisits;
    uint groups = scene.Mesh.CellGroups + mesh.CellGroups;
    if (mesh.Hit != 0u)
    {
        scene.Mesh = mesh;
        scene.Instance = instance;
    }
    scene.Mesh.TriangleTests = tests;
    scene.Mesh.FieldSamples = samples;
    scene.Mesh.CellVisits = visits;
    scene.Mesh.CellGroups = groups;
    scene.InstanceQueries++;
}

// Reads the same instance and 64-instance group bounds produced by Cluster scene preparation.
GiSceneHit TraceGiNearScene<T : IGiSceneMeshes>(T meshes, StructuredBuffer<float4> instanceBounds,
    StructuredBuffer<float4> groupBounds, StructuredBuffer<uint> instanceOrder,
    uint instanceCount, bool useGroups, float3 origin, float3 direction, float start, float limit)
{
    GiSceneHit hit = MissGiScene(limit);
    hit.Stage = 1u;
    uint groupCount = useGroups ? (instanceCount + 63u) / 64u : 1u;
    for (uint group = 0u; group < groupCount; group++)
    {
        uint first = 0u, end = instanceCount;
        if (useGroups)
        {
            float2 range = GiRayBox(groupBounds[group * 2u].xyz, groupBounds[group * 2u + 1u].xyz, origin, direction);
            hit.BoundsTests++;
            if (range.y < max(start, range.x) || range.x >= hit.Mesh.Distance) continue;
            first = group * 64u;
            end = min(first + 64u, instanceCount);
        }
        for (uint index = first; index < end; index++)
        {
            uint instance = useGroups ? instanceOrder[index] : index;
            float2 range = GiRayBox(instanceBounds[instance * 4u].xyz,
                instanceBounds[instance * 4u + 1u].xyz, origin, direction);
            hit.BoundsTests++;
            if (range.y < max(start, range.x) || range.x >= hit.Mesh.Distance) continue;
            GiMeshHit mesh = meshes.traceDistanceField(instance, origin, direction, start, hit.Mesh.Distance);
            MergeGiMeshHit(hit, mesh, instance);
        }
    }
    return hit;
}

float SampleGiGlobalDistance(GiGlobalField field, StructuredBuffer<float> distances, float3 position)
{
    float3 coordinate = clamp((position - field.Lower) / field.Pitch - 0.5,
        0.0.xxx, float3(field.Shape - 1u));
    uint3 first = uint3(floor(coordinate));
    float3 amount = frac(coordinate);
    float value = 0.0;
    for (uint corner = 0u; corner < 8u; corner++)
    {
        uint3 offset = uint3(corner & 1u, (corner >> 1u) & 1u, corner >> 2u);
        uint3 cell = min(first + offset, field.Shape - 1u);
        float3 weight = select(offset != 0u, amount, 1.0 - amount);
        uint index = cell.x + cell.y * field.Shape.x + cell.z * field.Shape.x * field.Shape.y;
        value += distances[index] * weight.x * weight.y * weight.z;
    }
    return value;
}

float3 GiGlobalNormal(GiGlobalField field, StructuredBuffer<float> distances,
    float3 position, float3 direction)
{
    float3 offset = float3(field.Pitch, 0.0, 0.0);
    float3 gradient = float3(
        SampleGiGlobalDistance(field, distances, position + offset.xyz) - SampleGiGlobalDistance(field, distances, position - offset.xyz),
        SampleGiGlobalDistance(field, distances, position + offset.yxz) - SampleGiGlobalDistance(field, distances, position - offset.yxz),
        SampleGiGlobalDistance(field, distances, position + offset.yzx) - SampleGiGlobalDistance(field, distances, position - offset.yzx));
    float square = dot(gradient, gradient);
    float3 normal = square > 1e-12 ? gradient * rsqrt(square) : -direction;
    return dot(normal, direction) > 0.0 ? -normal : normal;
}

// The far stage ends at the distance-field surface. Its result contains the
// position and normal used by the reflected-radiance cache.
GiDistanceHit TraceGiGlobalScene(GiGlobalField field, StructuredBuffer<float> distances,
    float3 origin, float3 direction, float start, float limit)
{
    GiDistanceHit hit = (GiDistanceHit)0;
    hit.Distance = limit;
    float2 box = GiRayBox(field.Lower, field.Upper, origin, direction);
    float travel = max(start, box.x), end = min(limit, box.y);
    float thickness = field.Pitch * 0.75 + field.Error;
    while (travel < end)
    {
        float3 position = origin + direction * travel;
        float distance = SampleGiGlobalDistance(field, distances, position);
        if (distance <= thickness)
        {
            hit.Normal = GiGlobalNormal(field, distances, position, direction);
            float alignment = max(1e-4, -dot(hit.Normal, direction));
            float surfaceDistance = max(start, travel + distance / alignment);
            if (surfaceDistance >= end) return hit;
            hit.Distance = surfaceDistance;
            hit.Position = origin + direction * surfaceDistance;
            hit.Hit = 1u;
            return hit;
        }
        travel += max(distance - thickness, field.Pitch * 0.2);
    }
    return hit;
}

#endif
