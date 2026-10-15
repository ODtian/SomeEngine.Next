struct ProjectedTriangle
{
    float4 EdgeA;
    float4 EdgeB;
    float4 EdgeC;
    uint TriangleId;
    uint ColorIndex;
};

struct TriangleBounds
{
    float3 Minimum;
    uint TriangleOrEnd;
    float3 Maximum;
    uint CoverageIndex;
};

bool IsTriangle(TriangleBounds node) { return (node.TriangleOrEnd & 0x80000000) != 0; }
uint GetTriangleIndex(TriangleBounds node) { return node.TriangleOrEnd & 0x7fffffff; }
uint GetSubtreeEnd(TriangleBounds node, uint nodeIndex) { return IsTriangle(node) ? nodeIndex + 1 : node.TriangleOrEnd; }

bool Contains(TriangleBounds node, float2 point)
{
    return all(point >= node.Minimum.xy) && all(point <= node.Maximum.xy);
}

bool Inside(float edge, float2 coefficients)
{
    return edge > 0 || (edge == 0 && (coefficients.x > 0 || (coefficients.x == 0 && coefficients.y < 0)));
}

bool Covers(ProjectedTriangle triangle, float2 point, out float depth)
{
    float3 barycentric = float3(dot(triangle.EdgeA.xyz, float3(point, 1)),
        dot(triangle.EdgeB.xyz, float3(point, 1)), dot(triangle.EdgeC.xyz, float3(point, 1)));
    depth = triangle.EdgeA.w + barycentric.y * (triangle.EdgeB.w - triangle.EdgeA.w) +
        barycentric.z * (triangle.EdgeC.w - triangle.EdgeA.w);
    return Inside(barycentric.x, triangle.EdgeA.xy) &&
        Inside(barycentric.y, triangle.EdgeB.xy) && Inside(barycentric.z, triangle.EdgeC.xy);
}
