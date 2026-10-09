using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterMeshPayloadTests
{
    [Fact]
    public void ClusterMeshRejectsUnalignedMaterialData()
    {
        const int dataOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes + 3;
        const int pageSize = dataOffset + sizeof(uint);
        byte[] payload = new byte[pageSize + Marshal.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            VertexBlobsOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes,
            IndicesOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes,
            ExtensionDataOffset = dataOffset,
            ExtensionDataSize = sizeof(uint),
            VertexEncodingHash = VertexEncodingCompatibility.Hash(StandardClusterVertexEncoder.EncodingCompatibility),
        };
        MemoryMarshal.Write(payload, in header);
        using var clusters = new ClusterMeshBlock
        {
            VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility,
        };

        Assert.Throws<InvalidDataException>(() => clusters.SetPayload(payload, pageSize));
    }
}
