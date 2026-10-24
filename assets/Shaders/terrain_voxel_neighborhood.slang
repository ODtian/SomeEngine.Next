struct VoxelNeighborhoodParameters
{
    int PageSize;
    int NeighborRadius;
    int SourcePageX;
    int SourcePageY;
    int SourcePageZ;
    int SourcePageWidth;
    int SourcePageHeight;
    int PageX;
    int PageY;
    int PageZ;
    int MinX;
    int MinY;
    int MinZ;
    int MaxX;
    int MaxY;
    int MaxZ;
    float InitialValue;
    float SampleSpacing;
    float CenterX;
    float CenterY;
    float CenterZ;
    float Radius;
    float Strength;
    float PlaneX;
    float PlaneY;
    float PlaneZ;
    float PlaneD;
};

ConstantBuffer<VoxelNeighborhoodParameters> Parameters;
StructuredBuffer<float> Voxels;
StructuredBuffer<int> PageOffsets;
RWStructuredBuffer<float> Mean;
RWStructuredBuffer<float> Result;

int GetPageCoordinate(int sample)
{
    return sample >= 0 ? sample / Parameters.PageSize : (sample + 1) / Parameters.PageSize - 1;
}

float ReadVoxel(int3 position)
{
    int size = Parameters.PageSize;
    int3 page = int3(GetPageCoordinate(position.x), GetPageCoordinate(position.y), GetPageCoordinate(position.z));
    int3 sourcePage = page - int3(Parameters.SourcePageX, Parameters.SourcePageY, Parameters.SourcePageZ);
    int offset = PageOffsets[(sourcePage.z * Parameters.SourcePageHeight + sourcePage.y) * Parameters.SourcePageWidth + sourcePage.x];
    int3 local = position - page * size;
    return offset < 0 ? Parameters.InitialValue : Voxels[offset + (local.z * size + local.y) * size + local.x];
}

bool IsWritten(int3 position)
{
    return all(position >= int3(Parameters.MinX, Parameters.MinY, Parameters.MinZ))
        && all(position < int3(Parameters.MaxX, Parameters.MaxY, Parameters.MaxZ));
}

float GetWeight(int3 position)
{
    float distance = length(float3(position) * Parameters.SampleSpacing - float3(Parameters.CenterX, Parameters.CenterY, Parameters.CenterZ));
    return Parameters.Strength * max(0.0f, 1.0f - distance / Parameters.Radius);
}

[shader("compute")]
[numthreads(4, 4, 4)]
void AverageVoxel(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId >= size)) return;
    int3 position = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) + int3(threadId);
    int sample = (threadId.z * size + threadId.y) * size + threadId.x;
    if (!IsWritten(position))
    {
        Mean[sample] = ReadVoxel(position);
        return;
    }
    float total = 0.0f;
    int count = 0;
    int3 minimum = position - Parameters.NeighborRadius;
    int3 maximum = position + Parameters.NeighborRadius;
    for (int z = minimum.z; z <= maximum.z; z++)
    for (int y = minimum.y; y <= maximum.y; y++)
    for (int x = minimum.x; x <= maximum.x; x++)
    {
        total += ReadVoxel(int3(x, y, z));
        count++;
    }
    Mean[sample] = total / count;
}

[shader("compute")]
[numthreads(4, 4, 4)]
void SmoothVoxel(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId >= size)) return;
    int3 position = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) + int3(threadId);
    int sample = (threadId.z * size + threadId.y) * size + threadId.x;
    float value = ReadVoxel(position);
    Result[sample] = IsWritten(position) ? lerp(value, Mean[sample], GetWeight(position)) : value;
}

[shader("compute")]
[numthreads(4, 4, 4)]
void FlattenVoxel(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId >= size)) return;
    int3 position = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) + int3(threadId);
    int sample = (threadId.z * size + threadId.y) * size + threadId.x;
    float value = ReadVoxel(position);
    float3 local = float3(position) * Parameters.SampleSpacing;
    float planeValue = dot(float3(Parameters.PlaneX, Parameters.PlaneY, Parameters.PlaneZ), local) + Parameters.PlaneD;
    Result[sample] = IsWritten(position) ? lerp(value, planeValue, GetWeight(position)) : value;
}
