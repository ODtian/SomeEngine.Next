#include "vertex_evaluate_interfaces.slang"
#include "material_interfaces.slang"

// Example material: normalized lift squared, displaced along the terrain height axis in world metres.
[MaterialScalars]
struct TerrainDisplacementScalars
{
    float4 BaseColorTint;
    float Displacement;
};

struct GroundVertex
{
    float3 Position;
    float2 TexCoord;
    float Lift;
    float3 Up;
};

struct GroundVertexEvaluate : IVertexEvaluate
{
    typedef GroundVertex Vertex;

    Vertex evaluate(VertexEvalContext context)
    {
        uint address = context.vertexBlobAddress + context.localVertexIndex * 24u;
        Vertex result;
        result.Position = TransformInstanceLocalPosition(asfloat(context.pageHeap.Load3(address)), context.transform);
        result.TexCoord = asfloat(context.pageHeap.Load2(address + 12u));
        result.Lift = saturate(asfloat(context.pageHeap.Load(address + 20u)));
        result.Up = normalize(TransformInstanceLocalPosition(float3(0, 1, 0), context.transform)
            - context.transform.Position);
        return result;
    }

    float3 getPosition(Vertex value) { return value.Position; }

    Vertex readLane(Vertex value, uint lane)
    {
        Vertex result;
        result.Position = WaveReadLaneAt(value.Position, lane);
        result.TexCoord = WaveReadLaneAt(value.TexCoord, lane);
        result.Lift = WaveReadLaneAt(value.Lift, lane);
        result.Up = WaveReadLaneAt(value.Up, lane);
        return result;
    }
};

struct GroundTriangle
{
    GroundVertex First;
    GroundVertex Second;
    GroundVertex Third;
    uint MaterialIndex;
};

struct GroundSurface
{
    float3 Position;
    float3 Normal;
    float2 TexCoord;
};

struct GroundDisplacement : IDomainEvaluate<GroundVertex>
{
    typedef GroundTriangle TriangleData;
    typedef GroundSurface Surface;

    TriangleData readLane(TriangleData triangle, uint lane)
    {
        GroundVertexEvaluate vertex;
        TriangleData result;
        result.First = vertex.readLane(triangle.First, lane);
        result.Second = vertex.readLane(triangle.Second, lane);
        result.Third = vertex.readLane(triangle.Third, lane);
        result.MaterialIndex = WaveReadLaneAt(triangle.MaterialIndex, lane);
        return result;
    }

    TriangleData makeTriangle(GroundVertex first, GroundVertex second, GroundVertex third, uint materialIndex)
    {
        TriangleData result;
        result.First = first;
        result.Second = second;
        result.Third = third;
        result.MaterialIndex = materialIndex;
        return result;
    }

    float3 cornerPosition(TriangleData triangle, uint corner)
    {
        if (corner == 0u) return triangle.First.Position;
        if (corner == 1u) return triangle.Second.Position;
        return triangle.Third.Position;
    }

    float GetDisplacement(TriangleData triangle)
    {
        TerrainDisplacementScalars values = LoadMaterialScalars<TerrainDisplacementScalars>(triangle.MaterialIndex);
        return values.Displacement;
    }

    float InterpolateLift(TriangleData triangle, float3 barycentric)
    {
        return triangle.First.Lift * barycentric.x + triangle.Second.Lift * barycentric.y
            + triangle.Third.Lift * barycentric.z;
    }

    float3 InterpolatePosition(TriangleData triangle, float3 barycentric)
    {
        return triangle.First.Position * barycentric.x + triangle.Second.Position * barycentric.y
            + triangle.Third.Position * barycentric.z;
    }

    float3 position(TriangleData triangle, float3 barycentric)
    {
        float lift = InterpolateLift(triangle, barycentric);
        return InterpolatePosition(triangle, barycentric) + triangle.First.Up * (GetDisplacement(triangle) * lift * lift);
    }

    Surface eval(TriangleData triangle, float3 barycentric)
    {
        float lift = InterpolateLift(triangle, barycentric);
        float displacement = GetDisplacement(triangle);
        float3 up = triangle.First.Up;
        float3 derivativeU = triangle.Second.Position - triangle.First.Position
            + up * (2.0f * displacement * lift * (triangle.Second.Lift - triangle.First.Lift));
        float3 derivativeV = triangle.Third.Position - triangle.First.Position
            + up * (2.0f * displacement * lift * (triangle.Third.Lift - triangle.First.Lift));
        Surface result;
        result.Position = InterpolatePosition(triangle, barycentric) + up * (displacement * lift * lift);
        result.Normal = normalize(cross(derivativeU, derivativeV));
        result.TexCoord = triangle.First.TexCoord * barycentric.x + triangle.Second.TexCoord * barycentric.y
            + triangle.Third.TexCoord * barycentric.z;
        return result;
    }

    SurfaceBarycentrics reconstruct(TriangleData triangle, SurfaceReconstructionInput input)
    {
        float3 edgeU = triangle.Second.Position - triangle.First.Position;
        float3 edgeV = triangle.Third.Position - triangle.First.Position;
        float3 up = triangle.First.Up;
        float determinant = dot(edgeU, cross(edgeV, up));
        float3 projectionU = cross(edgeV, up) / determinant;
        float3 projectionV = cross(up, edgeU) / determinant;
        float3 offset = input.TargetPosition - triangle.First.Position;
        float2 coordinates = float2(dot(offset, projectionU), dot(offset, projectionV));
        float3 offsetDx = input.TargetPositionDx - input.TargetPosition;
        float3 offsetDy = input.TargetPositionDy - input.TargetPosition;
        float2 dx = float2(dot(offsetDx, projectionU), dot(offsetDx, projectionV));
        float2 dy = float2(dot(offsetDy, projectionU), dot(offsetDy, projectionV));
        SurfaceBarycentrics result;
        result.Value = float3(1.0f - coordinates.x - coordinates.y, coordinates);
        result.Dx = float3(-dx.x - dx.y, dx);
        result.Dy = float3(-dy.x - dy.y, dy);
        result.Residual = length(position(triangle, result.Value) - input.TargetPosition);
        result.Valid = 1u;
        return result;
    }

    void bounds(TriangleData triangle, out float3 minimum, out float3 maximum)
    {
        patchBounds(triangle, float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1), minimum, maximum);
    }

    void patchBounds(TriangleData triangle, float3 bary0, float3 bary1, float3 bary2,
        out float3 minimum, out float3 maximum)
    {
        float3 first = InterpolatePosition(triangle, bary0);
        float3 second = InterpolatePosition(triangle, bary1);
        float3 third = InterpolatePosition(triangle, bary2);
        float3 lifts = float3(InterpolateLift(triangle, bary0), InterpolateLift(triangle, bary1),
            InterpolateLift(triangle, bary2));
        float lowLift = min(lifts.x, min(lifts.y, lifts.z));
        float highLift = max(lifts.x, max(lifts.y, lifts.z));
        float displacement = GetDisplacement(triangle);
        float3 firstOffset = triangle.First.Up * (displacement * lowLift * lowLift);
        float3 secondOffset = triangle.First.Up * (displacement * highLift * highLift);
        minimum = min(first, min(second, third)) + min(firstOffset, secondOffset);
        maximum = max(first, max(second, third)) + max(firstOffset, secondOffset);
    }

    DomainErrorBounds errorBounds(TriangleData triangle, float3 bary0, float3 bary1, float3 bary2,
        float3 position0, float3 position1, float3 position2)
    {
        float3 lifts = float3(InterpolateLift(triangle, bary0), InterpolateLift(triangle, bary1),
            InterpolateLift(triangle, bary2));
        float3 differences = float3(lifts.x - lifts.y, lifts.y - lifts.z, lifts.z - lifts.x);
        float width = max(lifts.x, max(lifts.y, lifts.z)) - min(lifts.x, min(lifts.y, lifts.z));
        float scale = .25f * abs(GetDisplacement(triangle)) * length(triangle.First.Up);
        DomainErrorBounds result;
        result.Edge = scale * differences * differences;
        result.Interior = scale * width * width;
        return result;
    }
};

struct GroundMaterial : ISurfaceEvaluate<GroundSurface, GroundTriangle>
{
    void evaluateSurface(SurfacePixelInput input, GroundSurface surface, float3 barycentric,
        GroundTriangle triangle, RWTexture2DArray<float4> outputColor)
    {
        TerrainDisplacementScalars values = LoadMaterialScalars<TerrainDisplacementScalars>(input.MaterialIndex);
        outputColor[input.PixelCoord] = values.BaseColorTint;
    }

    void evaluateDebug(uint debugMode, SurfacePixelInput input, GroundSurface surface, float3 barycentric,
        GroundTriangle triangle, RWTexture2DArray<float4> outputColor)
    {
        if (debugMode == 8u) outputColor[input.PixelCoord] = float4(barycentric, 1);
        else if (debugMode == 9u) outputColor[input.PixelCoord] = float4(surface.Normal * .5f + .5f, 1);
        else if (debugMode == 10u) outputColor[input.PixelCoord] = float4(frac(surface.TexCoord), 0, 1);
        else evaluateSurface(input, surface, barycentric, triangle, outputColor);
    }
};

typedef GroundVertexEvaluate VertexEvaluate;
typedef GroundDisplacement DomainEvaluate;
typedef OpaqueVisibilityEvaluate<GroundVertex> VisibilityEvaluate;
typedef GroundMaterial SurfaceEvaluate;
#define CLUSTER_VERTEX_ENCODING "example.terrain.position_uv_lift.v1"
#define CLUSTER_TESSELLATION_BOUNDS "finite"
#include "Pipeline/cluster_material.slang"
#include "cluster_tessellation_entries.slang"
