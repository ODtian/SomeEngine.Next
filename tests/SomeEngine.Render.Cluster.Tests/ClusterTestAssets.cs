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
    private const int PositionBytes = 3 * sizeof(ushort);
    private const int IndexBytes = 3;

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
            AssetWriter.Write(asset, path);
            streamed = await Mesh.OpenStreamedAsync(path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(AssetMetaFiles.GetMetaPath(path));
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
            AssetWriter.Write(asset, path);
            controlled = new ControlledRangeSource(FileRangeSource.Open(path), targetReadLength);
            Mesh streamed = await Mesh.OpenStreamedAsync(
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
            File.Delete(AssetMetaFiles.GetMetaPath(path));
        }
    }

    internal static ValueTask<PageLoadResult> LoadPageAsync(
        ClusterMeshes manager,
        uint pageId,
        CancellationToken cancellationToken = default)
        => manager.LoadPageIntoFinalOwnerAsync(pageId, cancellationToken);

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
        const uint vertexStride = 32;
        int pageBytes = checked(MeshPageHeader.Size + GPUCluster.SizeInBytes
            + PositionBytes + (int)vertexStride + IndexBytes);
        byte[] payload = new byte[pageBytes + Marshal.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes)),
            AttributesOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes + PositionBytes)),
            IndicesOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes
                + PositionBytes + vertexStride)),
            VertexStride = vertexStride,
            QuantStep = 1,
        };
        MemoryMarshal.Write(payload.AsSpan(), in header);

        var cluster = new GPUCluster
        {
            LODRadius = 1,
            PackedCenterZRadius = 1u << 16,
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
            BoundMax = Vector3.One,
        };
        MemoryMarshal.Write(
            payload.AsSpan(MeshPageHeader.Size, GPUCluster.SizeInBytes),
            in cluster);

        var leaf = new ClusterBVHNode
        {
            ChildPointer = 0,
            NodeType = 1,
        };
        leaf.SetLeafData(clusterStart: 0, clusterCount: 1);
        MemoryMarshal.Write(payload.AsSpan(pageBytes), in leaf);

        return new Mesh
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = name,
            Bounds = new Bounds { Center = new Vec3(), Radius = 1 },
            Payload = payload,
            VertexStride = vertexStride,
            BvhOffset = checked((ulong)pageBytes),
            QuantStep = 1,
        };
    }
}
