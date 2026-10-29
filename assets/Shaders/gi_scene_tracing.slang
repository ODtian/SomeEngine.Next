#ifndef GI_SCENE_TRACING_SLANG
#define GI_SCENE_TRACING_SLANG

#include "gi_mesh_distance_field.slang"

interface IGiSceneMeshes
{
    GiMeshHit traceDistanceField(uint instance, float3 origin, float3 direction, float start, float limit);
    GiMeshHit traceTriangles(uint instance, float3 origin, float3 direction, float start, float limit);
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
    uint3 ObjectShape;
    uint Padding1;
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

GiSceneHit LocateGiGlobalSurface<T : IGiSceneMeshes>(T meshes, GiGlobalField field,
    StructuredBuffer<uint> objectHeads, StructuredBuffer<uint2> objectLinks,
    StructuredBuffer<float4> instanceBounds, float3 origin, float3 direction, float start, float limit)
{
    float3 pitch = (field.Upper - field.Lower) / float3(field.ObjectShape);
    float3 position = origin + direction * ((start + limit) * 0.5);
    uint3 cell = uint3(clamp(floor((position - field.Lower) / pitch), 0.0.xxx, float3(field.ObjectShape - 1u)));
    uint index = cell.x + cell.y * field.ObjectShape.x + cell.z * field.ObjectShape.x * field.ObjectShape.y;
    uint link = objectHeads[index];
    GiSceneHit hit = MissGiScene(limit);
    hit.Stage = 2u;
    while (link != 0xffffffffu)
    {
        uint2 value = objectLinks[link];
        link = value.y;
        uint instance = value.x;
        float2 range = GiRayBox(instanceBounds[instance * 4u].xyz,
            instanceBounds[instance * 4u + 1u].xyz, origin, direction);
        hit.BoundsTests++;
        if (range.y < max(start, range.x) || range.x >= hit.Mesh.Distance) continue;
        GiMeshHit mesh = meshes.traceTriangles(instance, origin, direction, start, hit.Mesh.Distance);
        MergeGiMeshHit(hit, mesh, instance);
    }
    return hit;
}

// The Global SDF skips empty space. Object Grid supplies objects only in a potential surface interval.
GiSceneHit TraceGiGlobalScene<T : IGiSceneMeshes>(T meshes, GiGlobalField field,
    StructuredBuffer<float> globalDistances, StructuredBuffer<uint> objectHeads,
    StructuredBuffer<uint2> objectLinks, StructuredBuffer<float4> instanceBounds,
    float3 origin, float3 direction, float start, float limit)
{
    GiSceneHit hit = MissGiScene(limit);
    hit.Stage = 2u;
    float2 box = GiRayBox(field.Lower, field.Upper, origin, direction);
    float travel = max(start, box.x), end = min(limit, box.y);
    float3 objectPitch = (field.Upper - field.Lower) / float3(field.ObjectShape);
    while (travel < end)
    {
        float3 position = origin + direction * travel;
        uint3 cell = uint3(clamp(floor((position - field.Lower) / field.Pitch), 0.0.xxx, float3(field.Shape - 1u)));
        uint index = cell.x + cell.y * field.Shape.x + cell.z * field.Shape.x * field.Shape.y;
        float3 center = field.Lower + (float3(cell) + 0.5) * field.Pitch;
        float distance = globalDistances[index] - length(position - center) - field.Error;
        hit.Mesh.FieldSamples++;
        if (distance > field.Error)
        {
            travel += distance;
            continue;
        }
        float3 objectCell = floor((position - field.Lower) / objectPitch);
        float3 boundary = field.Lower + (objectCell + select(direction >= 0.0, 1.0.xxx, 0.0.xxx)) * objectPitch;
        float3 exits = select(direction != 0.0, (boundary - origin) / direction, asfloat(0x7f800000u).xxx);
        float next = min(end, max(travel + field.Error, min(exits.x, min(exits.y, exits.z))));
        GiSceneHit local = LocateGiGlobalSurface(meshes, field, objectHeads, objectLinks, instanceBounds,
            origin, direction, max(start, travel - field.Error), min(limit, next + field.Error));
        hit.Mesh.TriangleTests += local.Mesh.TriangleTests;
        hit.Mesh.FieldSamples += local.Mesh.FieldSamples;
        hit.Mesh.CellVisits += local.Mesh.CellVisits + 1u;
        hit.Mesh.CellGroups += local.Mesh.CellGroups;
        hit.InstanceQueries += local.InstanceQueries;
        hit.BoundsTests += local.BoundsTests;
        if (local.Mesh.Hit != 0u)
        {
            hit.Mesh.Hit = 1u;
            hit.Mesh.Distance = local.Mesh.Distance;
            hit.Mesh.Barycentric = local.Mesh.Barycentric;
            hit.Mesh.Triangle = local.Mesh.Triangle;
            hit.Instance = local.Instance;
            return hit;
        }
        travel = next + field.Error * 0.1;
    }
    return hit;
}

// Screen tracing supplies its confirmed distance. The caller retains its hit pixel for radiance reuse.
// Near queries confirm the interval before that candidate; Global SDF receives only the far segment.
GiSceneHit TraceGiScene<T : IGiSceneMeshes>(T meshes, GiSceneHit screen,
    GiGlobalField field, StructuredBuffer<float> globalDistances,
    StructuredBuffer<uint> objectHeads, StructuredBuffer<uint2> objectLinks,
    StructuredBuffer<float4> instanceBounds, StructuredBuffer<float4> groupBounds,
    StructuredBuffer<uint> instanceOrder, uint instanceCount, bool useGroups,
    float3 origin, float3 direction, float start, float limit, float nearDistance)
{
    float nearEnd = min(nearDistance, screen.Mesh.Hit != 0u ? screen.Mesh.Distance : limit);
    GiSceneHit near = TraceGiNearScene(meshes, instanceBounds, groupBounds, instanceOrder,
        instanceCount, useGroups, origin, direction, start, nearEnd);
    if (near.Mesh.Hit != 0u) return near;
    if (screen.Mesh.Hit != 0u)
    {
        screen.Mesh.TriangleTests += near.Mesh.TriangleTests;
        screen.Mesh.FieldSamples += near.Mesh.FieldSamples;
        screen.Mesh.CellVisits += near.Mesh.CellVisits;
        screen.Mesh.CellGroups += near.Mesh.CellGroups;
        screen.InstanceQueries += near.InstanceQueries;
        screen.BoundsTests += near.BoundsTests;
        return screen;
    }
    if (limit <= nearDistance) return near;
    GiSceneHit far = TraceGiGlobalScene(meshes, field, globalDistances, objectHeads, objectLinks,
        instanceBounds, origin, direction, max(start, nearDistance), limit);
    far.Mesh.TriangleTests += near.Mesh.TriangleTests;
    far.Mesh.FieldSamples += near.Mesh.FieldSamples;
    far.Mesh.CellVisits += near.Mesh.CellVisits;
    far.Mesh.CellGroups += near.Mesh.CellGroups;
    far.InstanceQueries += near.InstanceQueries;
    far.BoundsTests += near.BoundsTests;
    return far;
}

#endif
