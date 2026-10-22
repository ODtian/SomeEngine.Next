using System.Numerics;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationSurfaceAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-Navigation-" + Guid.NewGuid().ToString("N"));
    public NavigationSurfaceAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(0f, 3)]
    [InlineData(4f, 7)]
    public async Task SavedSurfaceRetainsEachFloorSupportAfterAssetsClose(float height, int area)
    {
        NavigationGeometryTriangle[] collision =
        [
            new(new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), 3),
            new(new(0, 0, 0), new(4, 0, 4), new(4, 0, 0), 3),
            new(new(0, 4, 0), new(0, 4, 4), new(4, 4, 4), 7),
            new(new(0, 4, 0), new(4, 4, 4), new(4, 4, 0), 7),
        ];
        var geometry = NavigationGeometry.ReadTriangles<NavigationGeometryTriangle[], CollisionReader>(collision);
        string meshPath = Path.Combine(_directory, "walkway.mesh.asset");
        using (var mesh = new MeshAsset { Name = "walkway" })
        using (var block = new NavigationGeometryBlock())
        {
            block.SetGeometry(geometry);
            mesh.SetBlock(block);
            await mesh.SaveAsync(meshPath);
        }
        NavigationGeometry reopenedGeometry;
        using (var mesh = await MeshAsset.OpenAsync(meshPath))
            reopenedGeometry = await (await mesh.GetBlockAsync<NavigationGeometryBlock>()).CreateAsync();
        var input = new NavigationBuildInput([new(reopenedGeometry, Matrix4x4.Identity, 5)],
            new(new(0, -1, 0), new(4, 6, 4)),
            new() { CellSize = 0.5f, CellHeight = 0.1f, TileSize = 4, BorderSize = 0.5f });
        NavigationSurface built = NavigationSurfaceBuilder.Schedule(input).Complete().Surface;
        string surfacePath = Path.Combine(_directory, "walkway.navigation.asset");
        using (var asset = new NavigationAsset { Name = "walkway" })
        using (var block = new NavigationSurfaceBlock())
        {
            block.SetSurface(built);
            asset.SetBlock(block);
            await asset.SaveAsync(surfacePath);
        }
        NavigationSurface reopened;
        using (var asset = await NavigationAsset.OpenAsync(surfacePath))
            reopened = await (await asset.GetBlockAsync<NavigationSurfaceBlock>()).CreateAsync();

        Assert.True(reopened.TryGetSupport(new(2.25f, height + 0.2f, 1.25f), 0.5f, out var support));
        Assert.Equal(new NavigationSupport(new(2.25f, height, 1.25f), Vector3.UnitY, area,
            height == 0 ? 4f : float.PositiveInfinity), support);
    }

    private readonly struct CollisionReader : INavigationTriangleReader<NavigationGeometryTriangle[]>
    {
        public static int GetTriangleCount(in NavigationGeometryTriangle[] source) => source.Length;
        public static NavigationGeometryTriangle GetTriangle(in NavigationGeometryTriangle[] source, int index) => source[index];
    }
}
