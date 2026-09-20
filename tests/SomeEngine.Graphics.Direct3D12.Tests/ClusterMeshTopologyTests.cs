using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ClusterMeshTopologyTests
{
    [Theory]
    [InlineData(0, 256)]
    [InlineData(1, 32)]
    [InlineData(17, 64)]
    [InlineData(32, 128)]
    [InlineData(32, 256)]
    public void VertexRanksMatchSortedUniqueIndices(int active, int indexRange)
    {
        const string source = """
            #include "cluster_mesh_topology.slang"
            StructuredBuffer<uint> Input;
            RWStructuredBuffer<uint> Results;
            [shader("compute")]
            [numthreads(32, 1, 1)]
            void Evaluate(uint lane : SV_GroupIndex)
            {
                uint3 triangle = uint3(Input[lane * 3u], Input[lane * 3u + 1u], Input[lane * 3u + 2u]);
                bool active = lane < Input[96];
                ClusterMeshVertexMask mask = BuildClusterMeshVertexMask(triangle, active, lane);
                Results[lane * 4u] = ClusterMeshVertexCount(mask);
                for (uint corner = 0u; corner < 3u; corner++)
                    Results[lane * 4u + corner + 1u] = active
                        ? ClusterMeshVertexRank(mask, triangle[corner]) : 0xFFFFFFFFu;
            }
            """;
        uint[] boundaries = [0, 1, 30, 31, 32, 63, 64, 127, 128, 191, 223, 224, 254, 255];
        uint[] input = new uint[97];
        for (int index = 0; index < 96; index++)
            input[index] = (index < boundaries.Length ? boundaries[index] : (uint)(index * 47 + 19)) % (uint)indexRange;
        input[96] = (uint)active;
        uint[] expected = input.Take(active * 3).Distinct().Order().ToArray();
        uint[] result = Assert.Single(D3D12TestCompute.ExecuteSource(source, "Evaluate", new(1, 1, 1),
            [(input, ResourceBindingType.BufferSrv, 4), (new uint[128], ResourceBindingType.BufferUav, 4)]));
        for (int lane = 0; lane < 32; lane++)
        {
            Assert.Equal((uint)expected.Length, result[lane * 4]);
            for (int corner = 0; corner < 3; corner++)
                Assert.Equal(lane < active ? (uint)Array.IndexOf(expected, input[lane * 3 + corner]) : uint.MaxValue,
                    result[lane * 4 + corner + 1]);
        }
    }
}
