using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Importers;

public static partial class ClusterBuilder
{
    public static MeshAsset ProcessRaw(Vector3[] rawPos, List<RawAttribute> rawAttributes,
        uint[] rawIndices, IReadOnlyList<MeshMaterialSlot> materialSlots, string name,
        IClusterVertexEncoder? vertexEncoder = null)
    {
        Mesh mesh = ClusterMeshBuilder.Build(rawPos, rawAttributes, rawIndices, materialSlots.Count,
            vertexEncoder ?? StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Offline, name);
        return CreateAsset(mesh, materialSlots);
    }

    /// <summary>Stores completed Cluster geometry with its neutral bounds, regions and material references.</summary>
    public static MeshAsset CreateAsset(Mesh mesh, IReadOnlyList<MeshMaterialSlot> materialSlots)
    {
        mesh = mesh.CaptureData();
        using var geometry = new MeshBlock { Bounds = mesh.Bounds, Regions = mesh.Regions.ToArray() };
        using var materials = new MeshMaterialsBlock
        {
            Materials = materialSlots.Select(static material => new AssetId<MaterialAsset>(material.MaterialGuid)).ToArray(),
        };
        using var clusters = new ClusterMeshBlock
        {
            VertexEncoding = mesh.VertexEncoding,
            LowestDetailPageIndices = mesh.LowestDetailPageIndices.ToArray(),
        };
        byte[] payload = new byte[checked((int)mesh.BvhOffset + mesh.BvhByteLength)];
        for (int page = 0; page < mesh.PageCount; page++)
        {
            var description = mesh.PageDigests[page];
            mesh.ReadPage(page, payload.AsSpan((int)description.Offset, (int)description.Length));
        }
        mesh.Bvh.Span.CopyTo(payload.AsSpan((int)mesh.BvhOffset));
        clusters.SetPayload(payload, mesh.BvhOffset);
        var asset = new MeshAsset { Name = mesh.Name ?? string.Empty };
        try
        {
            asset.SetBlock(geometry);
            asset.SetBlock(clusters);
            asset.SetBlock(materials);
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
}
