#include "cluster_build.slang"

// Example encoding: float3 position, float2 texture coordinates, float lift (24 bytes).
struct GroundVertexEncoder : IClusterVertexEncode
{
    uint encode(ClusterVertexInput vertices, RWByteAddressBuffer destination, uint byteOffset)
    {
        for (uint vertex = 0u; vertex < vertices.Count; vertex++)
        {
            uint source = vertices.SourceIndices[vertex];
            uint address = byteOffset + vertex * 24u;
            destination.Store3(address, vertices.Positions.Load3(source * 12u));
            destination.Store3(address + 12u, vertices.Attributes.Load3(source * 12u));
        }
        return vertices.Count * 24u;
    }
};

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGroundCluster()
{
    GroundVertexEncoder encoder;
    BuildCluster(encoder);
}
