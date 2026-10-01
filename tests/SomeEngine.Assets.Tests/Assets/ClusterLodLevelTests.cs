using System.Runtime.InteropServices;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterLodLevelTests
{
    [Fact]
    public async Task CoarseAndFineGeometryMeetAtTheSameLodError()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using var asset = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            ClusterBuilderTestMaterials.Slots(), "LOD transitions");
        ClusterMeshBlock geometry = asset.GetBlock<ClusterMeshBlock>();
        using var mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        var coarseErrors = new Dictionary<int, float>();
        for (int page = 0; page < mesh.PageCount; page++)
        {
            byte[] bytes = new byte[checked((int)mesh.PageDigests[page].Length)];
            mesh.ReadPage(page, bytes);
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(bytes);
            ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(
                bytes.AsSpan(checked((int)header.ClustersOffset), checked((int)header.ClusterCount * PackedCluster.SizeInBytes)));
            foreach (PackedCluster cluster in clusters)
                if (((cluster.PackedCounts >> 16) & 0xff) != 0)
                    coarseErrors[cluster.GroupId] = (float)BitConverter.UInt16BitsToHalf(cluster.LODErrorHalf);
        }
        using var bvh = mesh.RetainBvh();
        ReadOnlySpan<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(bvh.ToArray());
        int transitions = 0;
        foreach (ClusterBVHNode node in nodes)
        {
            if (node.NodeType != 1 || node.BoundMax.W == 0) continue;
            int parent = (int)node.BoundMax.W - 1;
            Assert.Equal(coarseErrors[parent], node.LODError);
            transitions++;
        }
        Assert.True(transitions > 0, "The generated mesh must exercise coarse-to-fine LOD transitions.");
    }

    [Fact]
    public void ProcessedIcoSphereContainsLowerLodLevels()
    {
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using var meshAsset = ClusterBuilder.ProcessRaw(
            vertices,
            attributes,
            indices,
            ClusterBuilderTestMaterials.Slots(),
            "TestLOD");

        using var payload = meshAsset.GetBlock<SomeEngine.Assets.Schema.ClusterMeshBlock>().Chunks.Retain(SomeEngine.Assets.Schema.ClusterMeshBlock.PayloadChunkKey);
        (int[] levelCounts, int maxLevel, _) = ReadLevelCounts(payload.ToArray());

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
            uint clustersOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(16, 4));
            uint extensionDataOffset = MemoryMarshal.Read<uint>(pageSpan.Slice(28, 4));
            uint extensionDataSize = MemoryMarshal.Read<uint>(pageSpan.Slice(40, 4));
            uint pageSize = checked(extensionDataOffset + extensionDataSize);

            int clusterByteSize = Marshal.SizeOf<PackedCluster>();
            if (clustersOffset + clusterCount * clusterByteSize > pageSpan.Length)
                break;
            ReadOnlySpan<byte> clustersSpan = pageSpan.Slice((int)clustersOffset, (int)clusterCount * clusterByteSize);
            ReadOnlySpan<PackedCluster> clusters = MemoryMarshal.Cast<byte, PackedCluster>(clustersSpan);

            foreach (PackedCluster cluster in clusters)
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
