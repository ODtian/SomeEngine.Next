using System.Numerics;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ClusterMeshBaseGeometryTests
{
    [Fact]
    public async Task OpeningDisconnectedMeshAcquiresItsCompleteGeometry()
    {
        const int triangleCount = 4096;
        var positions = new Vector3[triangleCount * 3];
        var indices = new uint[positions.Length];
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            int first = triangle * 3;
            float x = (triangle % 64) * 2;
            float y = (triangle / 64) * 2;
            positions[first] = new(x, y, 0);
            positions[first + 1] = new(x + 1, y, 0);
            positions[first + 2] = new(x, y + 1, 0);
            for (int vertex = 0; vertex < 3; vertex++) indices[first + vertex] = (uint)(first + vertex);
        }
        using var asset = ClusterBuilder.ProcessRaw(positions, [], indices,
            ClusterBuilderTestMaterials.Slots(), "Disconnected triangles");
        var mesh = await asset.GetBlock<ClusterMeshBlock>().CreateAsync(asset.GetBlock<MeshBlock>(), asset.Name);

        Assert.True(mesh.PageCount > 1);
        Assert.All(Enumerable.Range(0, mesh.PageCount), page => Assert.True(mesh.TryGetPage(page, out _)));
    }
}
