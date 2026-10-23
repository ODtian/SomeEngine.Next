// Example attributes: a float3 surface normal and a float2 terrain-local UV.
ByteAddressBuffer TerrainSamples;
ByteAddressBuffer TerrainCounts;
RWByteAddressBuffer TerrainAttributes;

[shader("compute")]
[numthreads(64, 1, 1)]
void CreateGroundAttributes(uint3 threadId : SV_DispatchThreadID)
{
    uint vertex = threadId.x;
    if (vertex >= TerrainCounts.Load(0)) return;
    float3 position = asfloat(TerrainSamples.Load3(vertex * 24u));
    TerrainAttributes.Store3(vertex * 20u, TerrainSamples.Load3(vertex * 24u + 12u));
    TerrainAttributes.Store2(vertex * 20u + 12u, asuint(position.xz));
}
