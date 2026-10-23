struct HeightNeighborhoodParameters
{
    int PageSize;
    int GridWidth;
    int GridDepth;
    int NeighborRadius;
    int SourcePageX;
    int SourcePageZ;
    int SourcePageWidth;
    int PageX;
    int PageZ;
    int MinX;
    int MinZ;
    int MaxX;
    int MaxZ;
    float InitialHeight;
    float SampleSpacing;
    float CenterX;
    float CenterZ;
    float Radius;
    float Strength;
    float PlaneX;
    float PlaneY;
    float PlaneZ;
    float PlaneD;
};

ConstantBuffer<HeightNeighborhoodParameters> Parameters;
StructuredBuffer<float> Heights;
StructuredBuffer<int> PageOffsets;
RWStructuredBuffer<float> Mean;
RWStructuredBuffer<float> Result;

float ReadHeight(int2 position)
{
    int size = Parameters.PageSize;
    int2 page = position / size - int2(Parameters.SourcePageX, Parameters.SourcePageZ);
    int offset = PageOffsets[page.y * Parameters.SourcePageWidth + page.x];
    int2 local = position % size;
    return offset < 0 ? Parameters.InitialHeight : Heights[offset + local.y * size + local.x];
}

bool IsWritten(int2 position)
{
    return all(position >= int2(Parameters.MinX, Parameters.MinZ))
        && all(position < int2(Parameters.MaxX, Parameters.MaxZ));
}

float GetWeight(int2 position)
{
    float distance = length(float2(position) * Parameters.SampleSpacing - float2(Parameters.CenterX, Parameters.CenterZ));
    return Parameters.Strength * max(0.0f, 1.0f - distance / Parameters.Radius);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void AverageHeight(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId.xy >= size)) return;
    int2 position = int2(Parameters.PageX, Parameters.PageZ) + int2(threadId.xy);
    int sample = threadId.y * size + threadId.x;
    if (!IsWritten(position))
    {
        Mean[sample] = ReadHeight(position);
        return;
    }
    float total = 0.0f;
    int count = 0;
    int2 minimum = max(int2(0, 0), position - Parameters.NeighborRadius);
    int2 maximum = min(int2(Parameters.GridWidth - 1, Parameters.GridDepth - 1), position + Parameters.NeighborRadius);
    for (int z = minimum.y; z <= maximum.y; z++)
    for (int x = minimum.x; x <= maximum.x; x++)
    {
        total += ReadHeight(int2(x, z));
        count++;
    }
    Mean[sample] = total / count;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void SmoothHeight(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId.xy >= size)) return;
    int2 position = int2(Parameters.PageX, Parameters.PageZ) + int2(threadId.xy);
    int sample = threadId.y * size + threadId.x;
    float height = ReadHeight(position);
    Result[sample] = IsWritten(position) ? lerp(height, Mean[sample], GetWeight(position)) : height;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void FlattenHeight(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId.xy >= size)) return;
    int2 position = int2(Parameters.PageX, Parameters.PageZ) + int2(threadId.xy);
    int sample = threadId.y * size + threadId.x;
    float height = ReadHeight(position);
    float2 local = float2(position) * Parameters.SampleSpacing;
    float planeHeight = -(Parameters.PlaneX * local.x + Parameters.PlaneZ * local.y + Parameters.PlaneD) / Parameters.PlaneY;
    Result[sample] = IsWritten(position) ? lerp(height, planeHeight, GetWeight(position)) : height;
}
