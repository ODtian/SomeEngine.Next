struct ComparisonSettings
{
    uint Width;
    uint Height;
    float MomentBias;
    float Overestimation;
};
ConstantBuffer<ComparisonSettings> Settings;

struct ForwardTriangle
{
    float4 A;
    float4 B;
    float4 C;
    uint TriangleId;
    uint ColorIndex;
};

struct Fragment
{
    float Depth;
    uint TriangleId;
    uint ColorIndex;
    uint NextIndex;
};

bool Before(Fragment a, Fragment b)
{
    return a.Depth < b.Depth || (a.Depth == b.Depth && a.TriangleId < b.TriangleId);
}
