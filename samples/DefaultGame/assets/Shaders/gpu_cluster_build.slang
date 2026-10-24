#include "cluster_build.slang"

// Example encoding: float3 position followed by float2 UV, 20 bytes per vertex.
struct ExampleVertexEncoder : IClusterVertexEncode
{
    uint encode(ClusterVertexInput vertices, RWByteAddressBuffer destination, uint byteOffset)
    {
        for (uint vertex = 0; vertex < vertices.Count; vertex++)
        {
            uint source = vertices.SourceIndices[vertex];
            destination.Store3(byteOffset + vertex * 20u, vertices.Positions.Load3(source * 12u));
            destination.Store2(byteOffset + vertex * 20u + 12u, vertices.Attributes.Load2(source * 8u));
        }
        return vertices.Count * 20u;
    }
};

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildExampleCluster()
{
    ExampleVertexEncoder encoder;
    BuildCluster(encoder);
}
