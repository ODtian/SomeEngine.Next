using System.Runtime.InteropServices;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterLodLevelTests
{
    [Fact]
    public void ProcessedIcoSphereContainsLowerLodLevels()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        var meshAsset = ClusterBuilder.ProcessRaw(vertices, attributes, indices, new List<string>(), "TestLOD");

        (int[] levelCounts, int maxLevel, _) = ReadLevelCounts(meshAsset.Payload!.Value);

        Assert.True(levelCounts[0] > 0, "Should have Level 0 clusters");
        Assert.True(
            maxLevel >= 3,
            $"Expected a useful multi-level hierarchy, but the highest generated level was {maxLevel}; " +
            $"counts=[{string.Join(", ", levelCounts)}].");
        Assert.True(levelCounts[1] > 0, "Should have Level 1 clusters");
        for (int level = 0; level <= maxLevel; level++)
            Assert.True(levelCounts[level] > 0, $"LOD level {level} should not be empty.");
    }

    private static (int[] Counts, int MaxLevel, int TotalClusters) ReadLevelCounts(ReadOnlyMemory<byte> payload)
    {
        int maxLevel = 0;
        int[] levelCounts = new int[16];
        int offset = 0;
        int payloadLength = payload.Length;
        int totalClusters = 0;

        while (offset < payloadLength)
        {
            ReadOnlySpan<byte> pageSpan = payload.Span[offset..];
            if (pageSpan.Length < MeshPageHeader.Size)
                break;

            uint clusterCount = MemoryMarshal.Read<uint>(pageSpan.Slice(0, 4));
            uint totalTriCount = MemoryMarshal.Read<uint>(pageSpan.Slice(8, 4));
            uint clustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));
            uint indicesOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(28, 4));
            uint pageSize = indicesOffset + totalTriCount * 3;

            int clusterByteSize = Marshal.SizeOf<GPUCluster>();
            if (clustersOffset + clusterCount * clusterByteSize > pageSpan.Length)
                break;
            ReadOnlySpan<byte> clustersSpan = pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            ReadOnlySpan<GPUCluster> clusters = MemoryMarshal.Cast<byte, GPUCluster>(clustersSpan);

            foreach (GPUCluster cluster in clusters)
            {
                int level = (int)((cluster.PackedCounts >> 16) & 0xFF);
                if ((uint)level < (uint)levelCounts.Length)
                    levelCounts[level]++;
                if (level > maxLevel)
                    maxLevel = level;
            }

            totalClusters += (int)clusterCount;
            offset += (int)pageSize;
        }

        return (levelCounts, maxLevel, totalClusters);
    }
}
