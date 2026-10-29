#ifndef GI_MESH_DISTANCE_FIELD_SLANG
#define GI_MESH_DISTANCE_FIELD_SLANG

struct GiMeshDistanceGrid
{
    uint3 Shape;
    uint FirstDistance;
    float3 Lower;
    float CellSize;
    float3 AxisX;
    float CenterX;
    float3 AxisY;
    float CenterY;
    float3 AxisZ;
    float CenterZ;
    float3 Upper;
    float DistanceError;
    float3 GeometryLower;
    uint FirstTriangle;
    float3 GeometryUpper;
    uint TriangleCount;
};

struct GiMeshTriangleGrid
{
    float3 Lower;
    uint Side;
    float3 Upper;
    uint FirstRange;
};

struct GiMeshHit
{
    float Distance;
    float3 Barycentric;
    uint3 Triangle;
    uint Hit;
    uint TriangleTests;
    uint FieldSamples;
    uint CellVisits;
    uint CellGroups;
};

// The caller specializes this with the material's existing vertex and domain evaluators.
// A GPU pointer addresses an existing hierarchy page reference, cluster and triangle.
// The caller follows that reference to the resident page; vertex bytes stay with that caller.
interface IGiMeshTriangles
{
    bool intersect(uint3 triangle, float3 origin, float3 direction, float start, float limit,
        out float distance, out float3 barycentric);
};

bool IntersectGiTriangle(float3 first, float3 second, float3 third, float3 origin,
    float3 direction, float start, float limit, out float distance, out float3 barycentric)
{
    float3 edgeU = second - first, edgeV = third - first;
    float3 perpendicular = cross(direction, edgeV);
    float determinant = dot(edgeU, perpendicular);
    distance = limit;
    barycentric = 0.0;
    if (determinant == 0.0) return false;
    float3 offset = origin - first;
    float u = dot(offset, perpendicular) / determinant;
    float3 crossOffset = cross(offset, edgeU);
    float v = dot(direction, crossOffset) / determinant;
    float t = dot(edgeV, crossOffset) / determinant;
    if (u < 0.0 || v < 0.0 || u + v > 1.0 || t <= start || t >= limit) return false;
    distance = t;
    barycentric = float3(1.0 - u - v, u, v);
    return true;
}

struct GiGridRay
{
    int3 Cell;
    int3 Step;
    float3 Next;
    float3 Delta;
};

float2 GiRayBox(float3 lower, float3 upper, float3 origin, float3 direction)
{
    float3 a = (lower - origin) / direction;
    float3 b = (upper - origin) / direction;
    float3 low = min(a, b), high = max(a, b);
    return float2(max(low.x, max(low.y, low.z)), min(high.x, min(high.y, high.z)));
}

GiGridRay StartGiGridRay(float3 origin, float3 direction, float3 lower, float3 pitch,
    float start, uint3 shape)
{
    GiGridRay ray;
    ray.Cell = int3(clamp(floor((origin + direction * start - lower) / pitch),
        0.0.xxx, float3(shape - 1u)));
    ray.Step = select(direction >= 0.0, int3(1), int3(-1));
    float3 boundary = lower + (float3(ray.Cell) + select(direction >= 0.0, 1.0.xxx, 0.0.xxx)) * pitch;
    ray.Next = select(direction != 0.0, (boundary - origin) / direction, asfloat(0x7f800000u).xxx);
    ray.Delta = abs(pitch / direction);
    return ray;
}

void AdvanceGiGridRay(inout GiGridRay ray)
{
    uint axis = ray.Next.y < ray.Next.x ? 1u : 0u;
    if (ray.Next.z < ray.Next[axis]) axis = 2u;
    ray.Cell[axis] += ray.Step[axis];
    ray.Next[axis] += ray.Delta[axis];
}

GiMeshHit MissGiMesh(float limit)
{
    GiMeshHit hit = (GiMeshHit)0;
    hit.Distance = limit;
    return hit;
}

GiMeshHit TestGiMeshTriangle<T : IGiMeshTriangles>(ByteAddressBuffer fields, uint pointers,
    uint triangle, T triangles, float3 origin, float3 direction, float start, GiMeshHit hit)
{
    uint3 pointer = fields.Load3(pointers + triangle * 12u);
    float distance;
    float3 barycentric;
    hit.TriangleTests++;
    if (triangles.intersect(pointer, origin, direction, start, hit.Distance, distance, barycentric))
    {
        hit.Distance = distance;
        hit.Barycentric = barycentric;
        hit.Triangle = pointer;
        hit.Hit = 1u;
    }
    return hit;
}

GiMeshHit TraceGiMeshTriangleList<T : IGiMeshTriangles>(ByteAddressBuffer fields, uint4 offsets,
    uint2 list, T triangles, float3 origin, float3 direction, float start, GiMeshHit hit)
{
    if (list.y != 0u) hit.CellGroups++;
    for (uint index = 0u; index < list.y; index++)
    {
        uint triangle = fields.Load(offsets.z + (list.x + index) * 4u);
        hit = TestGiMeshTriangle(fields, offsets.w, triangle, triangles, origin, direction, start, hit);
    }
    return hit;
}

GiMeshHit TraceGiMeshDenseCells<T : IGiMeshTriangles>(ByteAddressBuffer fields, uint4 offsets,
    uint2 page, float3 lower, float3 pitch, T triangles, float3 origin, float3 direction,
    float start, float entry, float end, GiMeshHit hit)
{
    uint side = page.y & 65535u;
    GiGridRay ray = StartGiGridRay(origin, direction, lower, pitch / float(side), entry, side.xxx);
    for (uint step = 0u; step < side * 3u + 3u; step++)
    {
        if (any(ray.Cell < 0) || any(ray.Cell >= int(side))) return hit;
        uint3 cell = uint3(ray.Cell);
        float next = min(ray.Next.x, min(ray.Next.y, ray.Next.z));
        hit.CellVisits++;
        uint index = page.x + cell.x + cell.y * side + cell.z * side * side;
        uint2 list = fields.Load2(offsets.y + index * 8u);
        hit = TraceGiMeshTriangleList(fields, offsets, list, triangles, origin, direction, start, hit);
        if ((hit.Hit != 0u && hit.Distance <= next) || next >= end) return hit;
        AdvanceGiGridRay(ray);
    }
    return hit;
}

// Complete triangle-grid query, also used inside the Global SDF / Object Grid branch.
GiMeshHit TraceGiMeshCells<T : IGiMeshTriangles>(ByteAddressBuffer fields, uint address,
    T triangles, float3 origin, float3 direction, float start, float limit)
{
    uint4 offsets = fields.Load4(address + 160u) + address;
    GiMeshHit hit = MissGiMesh(limit);
    uint triangleCount = fields.Load(address + 124u);
    if (triangleCount <= 4u)
    {
        uint first = fields.Load(address + 108u);
        for (uint triangle = 0u; triangle < triangleCount; triangle++)
            hit = TestGiMeshTriangle(fields, offsets.w, first + triangle, triangles,
                origin, direction, start, hit);
        return hit;
    }
    GiMeshTriangleGrid grid = fields.Load<GiMeshTriangleGrid>(address + 128u);
    float2 box = GiRayBox(grid.Lower, grid.Upper, origin, direction);
    float entry = max(start, box.x), end = min(limit, box.y);
    if (entry >= end) return hit;
    float3 pitch = (grid.Upper - grid.Lower) / float(grid.Side);
    GiGridRay ray = StartGiGridRay(origin, direction, grid.Lower, pitch, entry, grid.Side.xxx);
    float travel = entry;
    for (uint step = 0u; step < grid.Side * 3u + 3u; step++)
    {
        if (any(ray.Cell < 0) || any(ray.Cell >= int(grid.Side))) return hit;
        uint3 cell = uint3(ray.Cell);
        float next = min(ray.Next.x, min(ray.Next.y, ray.Next.z));
        hit.CellVisits++;
        uint index = grid.FirstRange + cell.x + cell.y * grid.Side + cell.z * grid.Side * grid.Side;
        uint2 list = fields.Load2(offsets.y + index * 8u);
        if ((list.y & 0x80000000u) != 0u)
            hit = TraceGiMeshDenseCells(fields, offsets, list, grid.Lower + float3(cell) * pitch,
                pitch, triangles, origin, direction, start, travel, min(next, end), hit);
        else
            hit = TraceGiMeshTriangleList(fields, offsets, list, triangles, origin, direction, start, hit);
        if ((hit.Hit != 0u && hit.Distance <= next) || next >= end) return hit;
        AdvanceGiGridRay(ray);
        travel = next;
    }
    return hit;
}

// Original near-stage budget: up to eight conservative distance jumps, then the complete grid.
GiMeshHit TraceGiMeshDistanceField<T : IGiMeshTriangles>(ByteAddressBuffer fields, uint address,
    T triangles, float3 origin, float3 direction, float start, float limit)
{
    GiMeshDistanceGrid grid = fields.Load<GiMeshDistanceGrid>(address);
    uint4 offsets = fields.Load4(address + 160u) + address;
    GiMeshHit hit = MissGiMesh(limit);
    if (grid.TriangleCount <= 4u)
        return TraceGiMeshCells(fields, address, triangles, origin, direction, start, limit);
    float3 center = float3(grid.CenterX, grid.CenterY, grid.CenterZ);
    float3 offset = origin - center;
    float3 fieldOrigin = float3(dot(offset, grid.AxisX), dot(offset, grid.AxisY), dot(offset, grid.AxisZ));
    float3 fieldDirection = float3(dot(direction, grid.AxisX), dot(direction, grid.AxisY), dot(direction, grid.AxisZ));
    float2 box = GiRayBox(grid.Lower, grid.Upper, fieldOrigin, fieldDirection);
    float travel = max(start, box.x), end = min(limit, box.y);
    if (travel >= end) return hit;
    float speed = length(fieldDirection);
    for (uint step = 0u; step < 8u; step++)
    {
        float3 point = fieldOrigin + fieldDirection * travel;
        uint3 cell = uint3(clamp(floor((point - grid.Lower) / grid.CellSize), 0.0.xxx, float3(grid.Shape - 1u)));
        float3 sampleCenter = grid.Lower + (float3(cell) + 0.5) * grid.CellSize;
        uint index = grid.FirstDistance + cell.x + cell.y * grid.Shape.x + cell.z * grid.Shape.x * grid.Shape.y;
        float value = asfloat(fields.Load(offsets.x + index * 4u));
        float distance = value - length(point - sampleCenter) - grid.DistanceError;
        hit.FieldSamples++;
        if (distance <= grid.DistanceError) break;
        travel += distance / speed;
        if (travel >= end) return hit;
    }
    uint samples = hit.FieldSamples;
    hit = TraceGiMeshCells(fields, address, triangles, origin, direction, travel, limit);
    hit.FieldSamples += samples;
    return hit;
}

#endif
