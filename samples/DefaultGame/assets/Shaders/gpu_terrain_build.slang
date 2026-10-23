#include "cluster_build.slang"

// Example encoding: float3 position, float3 normal, float2 UV.
struct GroundVertexEncoder : IClusterVertexEncode
{
    uint encode(ClusterVertexInput vertices, RWByteAddressBuffer destination, uint byteOffset)
    {
        for (uint vertex = 0; vertex < vertices.Count; vertex++)
        {
            uint source = vertices.SourceIndices[vertex];
            uint address = byteOffset + vertex * 32u;
            destination.Store3(address, vertices.Positions.Load3(source * 12u));
            destination.Store3(address + 12u, vertices.Attributes.Load3(source * 20u));
            destination.Store2(address + 24u, vertices.Attributes.Load2(source * 20u + 12u));
        }
        return vertices.Count * 32u;
    }
};

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGroundCluster()
{
    GroundVertexEncoder encoder;
    BuildCluster(encoder);
}
