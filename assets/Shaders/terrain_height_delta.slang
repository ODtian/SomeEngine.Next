struct HeightDeltaParameters
{
    int PageSize;
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
    float Delta;
};

ConstantBuffer<HeightDeltaParameters> Parameters;
StructuredBuffer<float> Heights;
StructuredBuffer<int> PageOffsets;
RWStructuredBuffer<float> Result;

[shader("compute")]
[numthreads(8, 8, 1)]
void AddHeightDelta(uint3 threadId : SV_DispatchThreadID)
{
    int size = Parameters.PageSize;
    if (threadId.x >= size || threadId.y >= size) return;
    int x = Parameters.PageX + threadId.x;
    int z = Parameters.PageZ + threadId.y;
    int page = (z / size - Parameters.SourcePageZ) * Parameters.SourcePageWidth
        + x / size - Parameters.SourcePageX;
    int offset = PageOffsets[page];
    int sample = threadId.y * size + threadId.x;
    float height = offset < 0 ? Parameters.InitialHeight : Heights[offset + sample];
    bool inside = x >= Parameters.MinX && x < Parameters.MaxX
        && z >= Parameters.MinZ && z < Parameters.MaxZ;
    Result[sample] = height + (inside ? Parameters.Delta : 0.0f);
}
