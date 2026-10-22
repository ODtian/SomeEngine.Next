#include "cluster_build.slang"

// Example encoding: float3 position followed by float2 UV, 20 bytes per vertex.
struct ExampleVertexEncoder : IClusterVertexEncode
{
    uint encode(ByteAddressBuffer positions, ByteAddressBuffer attributes, uint vertexCount,
        RWByteAddressBuffer destination, uint byteOffset)
    {
        for (uint vertex = 0; vertex < vertexCount; vertex++)
        {
            destination.Store3(byteOffset + vertex * 20u, positions.Load3(vertex * 12u));
            destination.Store2(byteOffset + vertex * 20u + 12u, attributes.Load2(vertex * 8u));
        }
        return vertexCount * 20u;
    }
};

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildExampleCluster()
{
    ExampleVertexEncoder encoder;
    BuildCluster(encoder);
}
