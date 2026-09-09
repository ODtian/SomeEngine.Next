using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ClusterTestAssets
{
    internal const string StandardVertexEncoding = "someengine.vertex.standard.cluster.v1";
    internal const int VertexBlobBytes = 1;
    internal const int IndexBytes = 3;
    internal const int PageBytes = MeshPageHeader.Size + PackedCluster.SizeInBytes
        + VertexBlobBytes + IndexBytes;

    internal static async ValueTask<ClusterMeshRegistration> AddAuthoredMeshAsync(
        this ClusterMeshes manager,
        Mesh asset)
    {
        using Mesh mesh = await OpenRuntimeMeshAsync(asset);
        return await manager.AddMeshAsync(mesh);
    }

    internal static async ValueTask<Mesh> OpenRuntimeMeshAsync(Mesh asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"SomeEngine-Cluster-{Guid.NewGuid():N}.mesh.asset");
        Mesh streamed;
        try
        {
            ClusterTestFiles.WriteMesh(asset, path);
            streamed = await ClusterTestFiles.OpenMeshAsync(path);
        }
        finally
        {
            File.Delete(path);
        }

        return streamed;
    }

    internal static async ValueTask<ControlledRuntimeMesh> OpenControlledRuntimeMeshAsync(
        Mesh asset,
        int targetReadLength)
    {
        ArgumentNullException.ThrowIfNull(asset);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"SomeEngine-Cluster-{Guid.NewGuid():N}.mesh.asset");
        ControlledRangeSource? controlled = null;
        try
        {
            ClusterTestFiles.WriteMesh(asset, path);
            controlled = new ControlledRangeSource(FileRangeSource.Open(path), targetReadLength);
            Mesh streamed = await ClusterTestFiles.OpenMeshAsync(
                controlled,
                ownsSource: true);
            controlled.Arm();
            return new ControlledRuntimeMesh(streamed, controlled);
        }
        catch
        {
            if (controlled is not null)
                await controlled.DisposeAsync();
            throw;
        }
        finally
        {
            File.Delete(path);
        }
    }

    internal static ValueTask<PageLoadResult> LoadPageAsync(
        ClusterMeshes manager,
        uint pageId,
        CancellationToken cancellationToken = default)
        => manager.LoadPageIntoStagingAsync(pageId, cancellationToken);

    internal static T AssertSingle<T>(ReadOnlySpan<T> items)
    {
        Assert.Equal(1, items.Length);
        return items[0];
    }

    internal static ClusterMeshRoot AssertRoot(
        ReadOnlySpan<ClusterMeshRoot> roots,
        Mesh mesh)
    {
        ClusterMeshRoot match = default;
        int matchCount = 0;
        foreach (ClusterMeshRoot root in roots)
        {
            if (root.Mesh != mesh)
                continue;
            match = root;
            matchCount++;
        }

        Assert.Equal(1, matchCount);
        return match;
    }

    internal static Mesh CreateSinglePageMesh(string name)
    {
        byte[] payload = new byte[PageBytes + Marshal.SizeOf<ClusterBVHNode>()];
        CreatePageData().CopyTo(payload, 0);
        PackedCluster cluster = MemoryMarshal.Read<PackedCluster>(
            payload.AsSpan(MeshPageHeader.Size, PackedCluster.SizeInBytes));

        var leaf = new ClusterBVHNode
        {
            ChildPointer = 0,
            NodeType = 1,
        };
        leaf.SetLeafData(clusterStart: 0, clusterCount: 1);
        MemoryMarshal.Write(payload.AsSpan(PageBytes), in leaf);

        return new Mesh
        {
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1 },
            Payload = payload,
            VertexEncoding = StandardVertexEncoding,
            BvhOffset = PageBytes,
        };
    }

    internal static byte[] CreatePageData(byte marker = 0)
    {
        byte[] payload = new byte[PageBytes];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            VertexBlobBytes = VertexBlobBytes,
            ClustersOffset = MeshPageHeader.Size,
            VertexBlobsOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes,
            IndicesOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes + VertexBlobBytes,
            ExtensionDataOffset = PageBytes,
            VertexEncodingHash = VertexEncodingCompatibility.Hash(StandardVertexEncoding),
        };
        MemoryMarshal.Write(payload.AsSpan(), in header);

        var cluster = new PackedCluster
        {
            VertexBlobSize = VertexBlobBytes,
            ExtensionOffset = uint.MaxValue,
            LODRadius = 1,
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
            BoundMax = marker == 0 ? Vector3.One : new Vector3(marker),
        };
        MemoryMarshal.Write(
            payload.AsSpan(MeshPageHeader.Size, PackedCluster.SizeInBytes),
            in cluster);

        payload[checked((int)header.VertexBlobsOffset)] = marker;
        return payload;
    }
}
