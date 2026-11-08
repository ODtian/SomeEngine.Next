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

struct GiMeshHit
{
    float Distance;
    float3 Barycentric;
    uint3 Triangle;
    uint Hit;
};

GiMeshHit MissGiMesh(float limit)
{
    GiMeshHit hit = (GiMeshHit)0;
    hit.Distance = limit;
    return hit;
}

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

float2 GiRayBox(float3 lower, float3 upper, float3 origin, float3 direction)
{
    float3 a = (lower - origin) / direction;
    float3 b = (upper - origin) / direction;
    float3 low = min(a, b), high = max(a, b);
    return float2(max(low.x, max(low.y, low.z)), min(high.x, min(high.y, high.z)));
}

// Each near-zero SDF cell directly addresses its complete surface neighborhood.
// Neighborhoods include the declared displacement envelope in mesh coordinates.
struct GiMeshSurfaceTrace
{
    float3 Origin;
    float3 Direction;
    float Travel;
    float End;
    float Speed;
    float Expansion;
    float Padding;
    uint SmallMesh;
};

GiMeshSurfaceTrace BeginGiMeshSurfaceTrace(ByteAddressBuffer fields, uint address,
    float3 origin, float3 direction, float start, float limit, float expansion)
{
    GiMeshDistanceGrid grid = fields.Load<GiMeshDistanceGrid>(address);
    GiMeshSurfaceTrace trace = (GiMeshSurfaceTrace)0;
    float3 offset = origin - float3(grid.CenterX, grid.CenterY, grid.CenterZ);
    trace.Origin = float3(dot(offset, grid.AxisX), dot(offset, grid.AxisY), dot(offset, grid.AxisZ));
    trace.Direction = float3(dot(direction, grid.AxisX), dot(direction, grid.AxisY), dot(direction, grid.AxisZ));
    float2 box = GiRayBox(grid.GeometryLower - expansion, grid.GeometryUpper + expansion,
        trace.Origin, trace.Direction);
    trace.Travel = max(start, box.x);
    trace.End = min(limit, box.y);
    trace.Speed = length(direction);
    trace.Expansion = expansion;
    trace.Padding = asfloat(fields.Load(address + 128u)) - expansion;
    trace.SmallMesh = grid.TriangleCount <= 4u ? grid.TriangleCount : 0u;
    return trace;
}

bool NextGiMeshSurface(ByteAddressBuffer fields, uint address,
    inout GiMeshSurfaceTrace trace, out uint2 references)
{
    references = 0u;
    if (trace.Travel >= trace.End) return false;
    if (trace.SmallMesh != 0u)
    {
        references = uint2(0u, trace.SmallMesh);
        trace.Travel = trace.End;
        return true;
    }
    GiMeshDistanceGrid grid = fields.Load<GiMeshDistanceGrid>(address);
    uint4 offsets = fields.Load4(address + 160u) + address;
    while (trace.Travel < trace.End)
    {
        float3 point = trace.Origin + trace.Direction * trace.Travel;
        uint3 cell = uint3(clamp(floor((point - grid.Lower) / grid.CellSize),
            0.0.xxx, float3(grid.Shape - 1u)));
        uint index = cell.x + cell.y * grid.Shape.x + cell.z * grid.Shape.x * grid.Shape.y;
        float3 lower = grid.Lower + float3(cell) * grid.CellSize;
        float3 center = lower + 0.5 * grid.CellSize;
        float distance = asfloat(fields.Load(offsets.x + (grid.FirstDistance + index) * 4u));
        float bound = distance - length(point - center) - grid.DistanceError - trace.Expansion;
        if (bound > trace.Padding)
        {
            trace.Travel += bound / trace.Speed;
            continue;
        }
        references = fields.Load2(offsets.y + index * 8u);
        float end = GiRayBox(lower - trace.Padding, lower + grid.CellSize + trace.Padding,
            trace.Origin, trace.Direction).y;
        trace.Travel = min(trace.End, end);
        return true;
    }
    return false;
}

#endif
