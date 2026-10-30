#ifndef GI_MATERIAL_ENTRIES_SLANG
#define GI_MATERIAL_ENTRIES_SLANG

#include "cluster_common.slang"
#include "gi_material_queries.slang"
#include "gi_emissive_data.slang"
#include "gi_mesh_triangles.slang"
#include "gi_geometry_pages.slang"

ByteAddressBuffer GiMeshFields;

struct GiTriangleVertices<TVertex>
{
    TVertex First;
    TVertex Second;
    TVertex Third;
    uint3 Indices;
    uint Page;
};

GiTriangleVertices<TVE.Vertex> ReadGiTriangle<TVE : IVertexEvaluate>(
    uniform TVE vertex, GiTriangleCandidate candidate)
{
    uint reference = GiHierarchy.Load(candidate.Triangle.x);
    uint page = (reference >> 8u) << 9u;
    uint cluster = page + PageHeap.Load(page + 16u) + candidate.Triangle.y * PACKED_CLUSTER_STRIDE_BYTES;
    uint blob = page + PageHeap.Load(page + 20u) + PageHeap.Load(cluster);
    uint firstTriangle = PageHeap.Load(cluster + 56u) >> 16u;
    uint firstIndex = page + PageHeap.Load(page + 24u) + (firstTriangle + candidate.Triangle.z) * 3u;
    GiTriangleVertices<TVE.Vertex> result;
    result.Page = page;
    result.Indices = uint3(GiMeshVertexIndex(PageHeap, firstIndex),
        GiMeshVertexIndex(PageHeap, firstIndex + 1u), GiMeshVertexIndex(PageHeap, firstIndex + 2u));
    VertexEvalContext context;
    context.pageHeap = PageHeap;
    context.instanceID = candidate.InstanceIndex;
    context.transform = GetCurrentInstanceTransform(candidate.InstanceIndex);
    context.visibleClusterIndex = 0xffffffffu;
    context.IsPreviousFrame = 0u;
    LoadVertexData(context, blob);
    context.localVertexIndex = result.Indices.x;
    result.First = vertex.evaluate(context);
    context.localVertexIndex = result.Indices.y;
    result.Second = vertex.evaluate(context);
    context.localVertexIndex = result.Indices.z;
    result.Third = vertex.evaluate(context);
    return result;
}

bool GiTriangleVisible<TVE, TVisibility>(uniform TVisibility visibility,
    GiTriangleVertices<TVE.Vertex> vertices, GiTriangleCandidate candidate, float3 barycentric)
    where TVE : IVertexEvaluate
    where TVisibility : IVisibilityEvaluate<TVE.Vertex>
{
    VisibilityVertexInput input = (VisibilityVertexInput)0;
    input.Source = MakeDrawRequest(vertices.Page, candidate.Triangle.y, candidate.InstanceIndex, 0u);
    input.LocalVertexIndex = vertices.Indices.x;
    TVisibility.Varying first = visibility.makeVarying(vertices.First, input);
    input.LocalVertexIndex = vertices.Indices.y;
    TVisibility.Varying second = visibility.makeVarying(vertices.Second, input);
    input.LocalVertexIndex = vertices.Indices.z;
    TVisibility.Varying third = visibility.makeVarying(vertices.Third, input);
    VisibilityInterpolationInput interpolation = (VisibilityInterpolationInput)0;
    interpolation.PerspectiveBarycentric = barycentric;
    interpolation.LinearBarycentric = barycentric;
    TVisibility.Varying varying = visibility.interpolatePixel(first, second, third, interpolation);
    VisibilityPixelInput pixel = (VisibilityPixelInput)0;
    pixel.Source = input.Source;
    pixel.TriangleID = candidate.Triangle.z;
    return visibility.evaluate(pixel, varying).Visible != 0u;
}

struct GiMaterialTriangles : IGiMeshTriangles
{
    VertexEvaluate Vertex;
    DomainEvaluate Domain;
    VisibilityEvaluate Visibility;
    GiMeshQuery Query;
    GiTraceRay Ray;

    bool intersect(uint3 pointer, float3 origin, float3 direction, float start, float limit,
        out float distance, out float3 barycentric)
    {
        distance = limit; barycentric = 0.0;
        uint page;
        if (!GetGiTrianglePage(pointer, Views[0].MaxPageFaults, page)) return false;
        uint material = GetClusterMaterialSlotOffset(Query.InstanceIndex) +
            GetClusterLocalMaterial(PageHeap, page, pointer.y, pointer.z);
        if (material != Query.MaterialIndex) return false;
        GiTriangleCandidate candidate = (GiTriangleCandidate)0;
        candidate.InstanceIndex = Query.InstanceIndex;
        candidate.MaterialIndex = material;
        candidate.Triangle = pointer;
        GiTriangleVertices<VertexEvaluate.Vertex> vertices = ReadGiTriangle(Vertex, candidate);
        DomainEvaluate.TriangleData triangle = Domain.makeTriangle(vertices.First, vertices.Second, vertices.Third, material);
        limit = min(limit, asfloat(GiClosestDistances[Query.RayIndex]));
        bool hit = IntersectGiTriangle(Domain.cornerPosition(triangle, 0u), Domain.cornerPosition(triangle, 1u),
            Domain.cornerPosition(triangle, 2u), Ray.Origin, Ray.Direction, start, limit, distance, barycentric);
        hit = hit && GiTriangleVisible<VertexEvaluate, VisibilityEvaluate>(Visibility, vertices, candidate, barycentric);
        if (hit) InterlockedMin(GiClosestDistances[Query.RayIndex], asuint(distance));
        return hit;
    }
};

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void IntersectGiMaterial(uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility, uint3 thread : SV_DispatchThreadID)
{
    uint2 work = GiMaterialWorkRanges[GiMaterialWork.MaterialIndex];
    if (thread.x >= work.y) return;
    uint index = GiMaterialWorkIndices[work.x + thread.x];
    GiMeshQuery query = GiMeshQueries[index];
    GiTraceRay ray = GiRays[query.RayIndex];
    GiMaterialTriangles triangles;
    triangles.Vertex = vertex; triangles.Domain = domain; triangles.Visibility = visibility;
    triangles.Query = query; triangles.Ray = ray;
    RenderInstanceTransform transform = GetCurrentInstanceTransform(query.InstanceIndex);
    float3 origin = InverseTransformInstancePosition(ray.Origin, transform);
    float3 direction = InverseTransformInstanceVector(ray.Direction, transform);
    float limit = min(query.Limit, asfloat(GiClosestDistances[query.RayIndex]));
    GiMeshHit hit = TraceGiMeshDistanceField(GiMeshFields, query.FieldAddress, triangles,
        origin, direction, query.Start, limit, query.MaterialIndex - GetClusterMaterialSlotOffset(query.InstanceIndex));
    GiTriangleCandidate candidate = (GiTriangleCandidate)0;
    candidate.RayIndex = query.RayIndex;
    candidate.InstanceIndex = query.InstanceIndex;
    candidate.MaterialIndex = query.MaterialIndex;
    candidate.Triangle = hit.Triangle;
    candidate.Start = query.Start; candidate.Limit = query.Limit;
    GiCandidates[index] = candidate;
    GiCandidateHits[index] = hit.Hit != 0u ? float4(hit.Distance, hit.Barycentric) : float4(query.Limit, 0.0, 0.0, 0.0);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void ShadeGiMaterial(uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility, uniform SurfaceEvaluate surface, uint3 thread : SV_DispatchThreadID)
{
    uint2 work = GiMaterialWorkRanges[GiMaterialWork.MaterialIndex];
    if (thread.x >= work.y) return;
    uint index = GiMaterialWorkIndices[work.x + thread.x];
    GiTriangleCandidate candidate = GiCandidates[index];
    GiTraceRay ray = GiRays[candidate.RayIndex];
    float4 hit = GiCandidateHits[index];
    GiTriangleVertices<VertexEvaluate.Vertex> vertices = ReadGiTriangle(vertex, candidate);
    DomainEvaluate.TriangleData triangle = domain.makeTriangle(vertices.First, vertices.Second, vertices.Third, candidate.MaterialIndex);
    DomainEvaluate.Surface value = domain.eval(triangle, hit.yzw);
    GiMaterialInput input;
    input.InstanceIndex = candidate.InstanceIndex;
    input.MaterialIndex = candidate.MaterialIndex;
    input.RayDirection = ray.Direction;
    input.Footprint = 0.0;
    GiMaterialSurface material = surface.evaluateGiSurface(input, value, hit.yzw, triangle);
    GiSurfaceSample sample = (GiSurfaceSample)0;
    sample.Position = domain.position(triangle, hit.yzw);
    sample.Distance = hit.x;
    sample.Normal = material.Normal;
    sample.Coverage = material.Coverage;
    if (!GiTriangleVisible<VertexEvaluate, VisibilityEvaluate>(visibility, vertices, candidate, hit.yzw)) sample.Coverage = 0.0;
    sample.Diffuse = material.Diffuse;
    sample.Occlusion = material.Occlusion;
    sample.Emission = material.Emission;
    sample.GeometricNormal = material.GeometricNormal;
    GiSurfaceSamples[index] = sample;
}

[shader("compute")]
[numthreads(1, 1, 1)]
void ClassifyGiMaterialEmission(uniform SurfaceEvaluate surface)
{
    float3 emission = max(surface.getGiEmissionBound(GiMaterialWork.MaterialIndex), 0.0);
    uint index = GiMaterialWork.MaterialIndex;
    float3 previous = GiEmissionBounds[((GiEmissive.Frame + 1u) & 1u) * GiEmissive.MaterialCount + index].rgb;
    GiEmissionBounds[(GiEmissive.Frame & 1u) * GiEmissive.MaterialCount + index] =
        float4(emission, any(emission != previous) ? 1.0 : 0.0);
    if (any(emission != previous))
        InterlockedOr(GiEmitterChanges[0], 6u | (any(emission > 0.0) != any(previous > 0.0) ? 1u : 0u));
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[shader("compute")]
[numthreads(64, 1, 1)]
void UpdateGiEmissiveTriangles(uniform VertexEvaluate vertex, uniform DomainEvaluate domain,
    uniform VisibilityEvaluate visibility, uniform SurfaceEvaluate surface, uint3 thread : SV_DispatchThreadID)
{
    uint2 work = GiMaterialWorkRanges[GiMaterialWork.MaterialIndex];
    if (thread.x >= work.y) return;
    uint index = GiMaterialWorkIndices[work.x + thread.x];
    GiTriangleCandidate candidate = GiCandidates[index];
    GiTriangleVertices<VertexEvaluate.Vertex> vertices = ReadGiTriangle(vertex, candidate);
    DomainEvaluate.TriangleData triangle = domain.makeTriangle(vertices.First, vertices.Second, vertices.Third, candidate.MaterialIndex);
    float3 first = domain.cornerPosition(triangle, 0u);
    float3 edgeU = domain.cornerPosition(triangle, 1u) - first;
    float3 edgeV = domain.cornerPosition(triangle, 2u) - first;
    float3 normal = cross(edgeU, edgeV);
    float area = length(normal) * 0.5;
    GiMaterialInput input;
    input.InstanceIndex = candidate.InstanceIndex;
    input.MaterialIndex = candidate.MaterialIndex;
    input.RayDirection = -normalize(normal);
    input.Footprint = 0.0;
    float2 sequence = frac(float2(0.7548776662, 0.5698402910) * float(index * 13u + GiEmissive.Frame + 1u));
    float root = sqrt(sequence.x);
    float3 barycentric = float3(1.0 - root, root * (1.0 - sequence.y), root * sequence.y);
    float3 emitted = 0.0;
    if (GiTriangleVisible<VertexEvaluate, VisibilityEvaluate>(visibility, vertices, candidate, barycentric))
    {
        GiMaterialSurface material = surface.evaluateGiSurface(input, domain.eval(triangle, barycentric), barycentric, triangle);
        emitted = material.Emission * material.Coverage;
    }
    float3 center = first + (edgeU + edgeV) / 3.0;
    float radius = max(length(first - center), max(length(first + edgeU - center), length(first + edgeV - center)));
    float4 bound = GiMaterialEmissionBound(candidate.MaterialIndex);
    if (any(abs(GiEmissiveTriangles[index].Emission.rgb - emitted) > max(emitted * 0.01, 1e-4.xxx)))
        InterlockedOr(GiEmitterChanges[0], 4u);
    float flux = area * max(GiEmissionBrightness(emitted), GiEmissionBrightness(bound.rgb) * 0.01);
    RenderInstanceTransform current = GetCurrentInstanceTransform(candidate.InstanceIndex);
    RenderInstanceTransform previous = GetPreviousInstanceTransform(candidate.InstanceIndex);
    bool changed = any(current.Position != previous.Position) || any(current.Rotation != previous.Rotation) ||
        current.Scale != previous.Scale || any(current.Stretch != previous.Stretch) ||
        bound.w > 0.0 ||
        any(abs(GiEmissiveTriangles[index].Emission.rgb - emitted) > max(emitted * 0.01, 1e-4.xxx));
    GiEmissiveTriangle result = (GiEmissiveTriangle)0;
    result.PositionArea = float4(first, area);
    result.EdgeUFlux = float4(edgeU, flux);
    result.EdgeV = float4(edgeV, 0.0);
    result.Emission = float4(emitted, 0.0);
    result.SamplePositionRadius = float4(domain.position(triangle, barycentric), radius);
    result.TriangleInstance = uint4(candidate.Triangle, candidate.InstanceIndex);
    result.Material.x = candidate.MaterialIndex;
    result.Material.y = changed ? 1u : 0u;
    GiEmissiveTriangles[index] = result;
}

#endif
