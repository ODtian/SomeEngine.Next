using System.Numerics;

namespace SomeEngine.Navigation.Tests;

public sealed class SurfaceBuildTests
{
    [Fact]
    public void HeightfieldReaderBuildsTheSourceSupportAndClassification()
    {
        var terrain = new TerrainData(2f, 9);
        NavigationGeometry geometry = NavigationGeometry.ReadHeightfield<TerrainData, TerrainReader>(terrain);
        var input = new NavigationBuildInput([new(geometry, Matrix4x4.Identity, 1)],
            new(new(0, 0, 0), new(2, 5, 2)), new() { CellSize = 0.5f });

        NavigationSurface surface = NavigationSurfaceBuilder.Schedule(input).Complete().Surface;

        Assert.True(surface.TryGetSupport(new(0.75f, 2.1f, 0.75f), 0.5f, out var support));
        Assert.Equal(new NavigationSupport(new(0.75f, 2, 0.75f), Vector3.UnitY, 9, float.PositiveInfinity), support);
    }

    [Theory]
    [InlineData(0f, 3)]
    [InlineData(4f, 7)]
    public void StackedWalkwaysProvideTheirOwnSupportHeightAndArea(float height, int area)
    {
        NavigationGeometry geometry = new(
            [new(0, 0, 0), new(0, 0, 4), new(4, 0, 4), new(4, 0, 0)],
            [new(0, 1, 2, 3), new(0, 2, 3, 3)]);
        NavigationGeometry upper = new(geometry.Positions, new NavigationTriangle[] { new(0, 1, 2, 7), new(0, 2, 3, 7) });
        Matrix4x4 placement = Matrix4x4.CreateScale(-1, 1, 1) * Matrix4x4.CreateTranslation(4, 0, 0);
        var input = new NavigationBuildInput(
            [new(geometry, placement, 1), new(upper, Matrix4x4.CreateTranslation(0, 4, 0), 2)],
            new(new(0, -1, 0), new(4, 6, 4)),
            new() { CellSize = 0.5f, CellHeight = 0.1f, TileSize = 4, BorderSize = 0.5f });

        NavigationSurface surface = NavigationSurfaceBuilder.Schedule(input).Complete().Surface;

        Assert.True(surface.TryGetSupport(new(2.25f, height + 0.2f, 1.25f), 0.5f, out var support));
        Assert.Equal(new NavigationSupport(new(2.25f, height, 1.25f), Vector3.UnitY, area,
            height == 0 ? 4f : float.PositiveInfinity), support);
    }

    private readonly record struct TerrainData(float Height, int Area);
    private readonly struct TerrainReader : INavigationHeightfieldReader<TerrainData>
    {
        public static int GetWidth(in TerrainData source) => 3;
        public static int GetDepth(in TerrainData source) => 3;
        public static Vector3 GetPosition(in TerrainData source, int x, int z) => new(x, source.Height, z);
        public static int GetArea(in TerrainData source, int x, int z) => source.Area;
    }
}
