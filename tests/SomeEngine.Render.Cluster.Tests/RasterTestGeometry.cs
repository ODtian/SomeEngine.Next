using System.Numerics;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Resources;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Render.Cluster.Tests;

internal static class RasterTestGeometry
{
    internal static async Task<Mesh> CreatePlaneAsync()
    {
        Vector3[] positions = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        List<RawAttribute> attributes =
        [
            new("NORMAL", new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 }, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, [0, 1, 2, 0, 2, 3],
            [new MeshMaterialSlot(Guid.Empty)], "Authored plane");
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock vertexAttributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), vertexAttributes, asset.Name);
        await geometry.LoadPagesAsync(mesh, vertexAttributes, Enumerable.Range(0, mesh.PageCount));
        return mesh;
    }

    internal static async Task<Mesh> CreateAsync()
    {
        Vector3[] positions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ];
        uint[] triangles = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
            2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        float[] normals = positions.SelectMany(static value => new[] { value.X, value.Y, value.Z }).ToArray();
        List<RawAttribute> attributes =
        [
            new("NORMAL", normals, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[positions.Length * 2], 2, GeometryValueType.Float16, 2, false),
        ];
        using MeshAsset asset = ClusterBuilder.ProcessRaw(positions, attributes, triangles,
            [new MeshMaterialSlot(Guid.Empty)], "Raster test octahedron");
        ClusterGeometryBlock geometry = asset.GetBlock<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock vertexAttributes = asset.GetBlock<ClusterVertexAttributesBlock>();
        Mesh mesh = await geometry.CreateAsync(asset.GetBlock<MeshBlock>(), vertexAttributes, asset.Name);
        await geometry.LoadPagesAsync(mesh, vertexAttributes, Enumerable.Range(0, mesh.PageCount));
        return mesh;
    }
}
