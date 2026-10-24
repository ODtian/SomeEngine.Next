struct VoxelDeltaParameters
{
    int PageSize;
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
    float Delta;
};

ConstantBuffer<VoxelDeltaParameters> Parameters;
StructuredBuffer<float> Voxels;
StructuredBuffer<int> PageOffsets;
RWStructuredBuffer<float> Result;

[shader("compute")]
[numthreads(4, 4, 4)]
void AddVoxelSphereDelta(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (any(threadId >= size)) return;
    int3 position = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) + int3(threadId);
    int3 page = int3(Parameters.PageX, Parameters.PageY, Parameters.PageZ) / size
        - int3(Parameters.SourcePageX, Parameters.SourcePageY, Parameters.SourcePageZ);
    int offset = PageOffsets[(page.z * Parameters.SourcePageHeight + page.y) * Parameters.SourcePageWidth + page.x];
    int sample = (threadId.z * size + threadId.y) * size + threadId.x;
    float value = offset < 0 ? Parameters.InitialValue : Voxels[offset + sample];
    bool inside = all(position >= int3(Parameters.MinX, Parameters.MinY, Parameters.MinZ))
        && all(position < int3(Parameters.MaxX, Parameters.MaxY, Parameters.MaxZ));
    float3 center = float3(Parameters.CenterX, Parameters.CenterY, Parameters.CenterZ);
    float weight = max(0.0f, 1.0f - distance(float3(position) * Parameters.SampleSpacing, center) / Parameters.Radius);
    Result[sample] = value + (inside ? Parameters.Delta * weight : 0.0f);
}
