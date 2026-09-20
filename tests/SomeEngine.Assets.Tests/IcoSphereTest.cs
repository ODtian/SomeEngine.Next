using System.IO;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using System.Linq;

namespace SomeEngine.Tests;

public class IcoSphereTest
{
    [Fact]
    public async Task TestIcoSphereGeneration()
    {
        // Level 3 subdivision -> 1280 triangles, 642 vertices
        // Level 4 subdivision -> 5120 triangles, 2562 vertices
        // Level 5 subdivision -> 20480 triangles, 10242 vertices
        var (vertices, indices, attributes) =
            PrimitiveMeshGenerator.CreateIcoSphere(5);

        Assert.Equal(20480 * 3, indices.Length);
        Assert.Equal(10242, vertices.Length);

        // Process through ClusterBuilder
        using var meshAsset = ClusterBuilder.ProcessRaw(
            vertices,
            attributes,
            indices,
            SomeEngine.Assets.Tests.Assets.ClusterBuilderTestMaterials.Slots(),
            "IcoSphere_LOD5"
        );

        Assert.NotNull(meshAsset.Payload);
        Assert.True(meshAsset.Payload.Value.Length > 0);

        // The runtime contract is one opaque raw vertex record, not a fixed attribute schema.
        Assert.Equal(StandardClusterVertexEncoder.EncodingCompatibility, meshAsset.VertexEncoding);

        bool sawPartialWord = false;
        int pageOffset = 0;
        while ((ulong)pageOffset < meshAsset.BvhOffset)
        {
            ReadOnlySpan<byte> payload = meshAsset.Payload.Value.Span[pageOffset..];
            var header = System.Runtime.InteropServices.MemoryMarshal.Read<SomeEngine.Assets.Data.MeshPageHeader>(payload);
            var clusters = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, SomeEngine.Assets.Data.PackedCluster>(
                payload.Slice((int)header.ClustersOffset, (int)header.ClusterCount * SomeEngine.Assets.Data.PackedCluster.SizeInBytes));
            foreach (ref readonly var cluster in clusters)
            {
                int address = checked((int)(header.VertexBlobsOffset + cluster.VertexBlobOffset));
                Assert.Equal(0, address & 3);
                Assert.Equal(0u, cluster.VertexBlobSize & 3u);
                uint vertexCount = BitConverter.ToUInt32(payload.Slice(address + 8, 4));
                uint stride = BitConverter.ToUInt32(payload.Slice(address + 16, 4));
                uint bits = BitConverter.ToUInt32(payload.Slice(address + 20, 4)) + vertexCount * stride;
                sawPartialWord |= (bits & 31u) != 0u;
                Assert.Equal(((bits + 31u) / 32u) * 4u, cluster.VertexBlobSize);
            }
            pageOffset += checked((int)(header.ExtensionDataOffset + header.ExtensionDataSize));
        }
        Assert.True(sawPartialWord);

        using var directory = new SomeEngine.Assets.Tests.Assets.AssetTestDirectory();
        string outputPath = directory.File("icosphere.mesh.asset");

        (meshAsset).SaveAsync(outputPath).AsTask().GetAwaiter().GetResult();
        using Mesh.Asset roundTrip = await Mesh.Asset.OpenAsync(outputPath);
        using var reopenedBytes = await roundTrip.ReadPayloadAsync();
        Assert.Equal(meshAsset.Payload.Value.ToArray(), reopenedBytes.Memory.ToArray());
    }

}
