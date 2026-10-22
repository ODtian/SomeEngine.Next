using System.Numerics;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationMeshAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationMesh-" + Guid.NewGuid().ToString("N"));
    public NavigationMeshAssetTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task RepeatedCreationsShareTheAcquiredMeshArrays()
    {
        var selection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
        var mesh = new NavigationMesh(
            [new(0, 0, 0), new(0, 0, 2), new(2, 0, 2), new(2, 0, 0)],
            [0, 1, 2, 3], [new(0, 4, 0, 0)], []);
        string path = Path.Combine(_directory, "shared.navigation.asset");
        using (var asset = new NavigationAsset())
        using (var block = new NavigationMeshBlock())
        {
            block.SetMesh(selection, mesh);
            asset.SetBlock(block);
            await asset.SaveAsync(path);
        }
        NavigationMesh first, second;
        using (var asset = await NavigationAsset.OpenAsync(path))
        {
            NavigationMeshBlock block = await asset.GetBlockAsync<NavigationMeshBlock>();
            using NavigationMeshBlock branch = block.Duplicate();
            first = await block.CreateAsync(selection);
            second = await branch.CreateAsync(selection);
        }

        Assert.True(first.Positions.Equals(second.Positions));
        Assert.True(first.PolygonVertices.Equals(second.PolygonVertices));
        Assert.True(first.Polygons.Equals(second.Polygons));
    }

    [Theory]
    [InlineData("a481162f-a448-4c4b-88b7-5c9eafcf9661", "main", 0f)]
    [InlineData("8735997d-07de-4e93-96e1-1779fecebd60", "main", 2f)]
    [InlineData("a481162f-a448-4c4b-88b7-5c9eafcf9661", "raised", 4f)]
    public async Task ReopenedMeshProvidesTheSelectedProfileAndBuildVariant(string profileId, string variant, float height)
    {
        AssetId<NavigationProfileAsset> firstProfile = new(Guid.Parse("a481162f-a448-4c4b-88b7-5c9eafcf9661"));
        AssetId<NavigationProfileAsset> secondProfile = new(Guid.Parse("8735997d-07de-4e93-96e1-1779fecebd60"));
        string path = Path.Combine(_directory, "walkway.navigation.asset");
        using (var asset = new NavigationAsset())
        using (var block = new NavigationMeshBlock())
        {
            block.SetMesh(new(firstProfile, "main"), CreateMesh(0));
            block.SetMesh(new(secondProfile, "main"), CreateMesh(2));
            block.SetMesh(new(firstProfile, "raised"), CreateMesh(4));
            asset.SetBlock(block);
            await asset.SaveAsync(path);
        }
        NavigationMesh restored;
        using (var asset = await NavigationAsset.OpenAsync(path))
            restored = await (await asset.GetBlockAsync<NavigationMeshBlock>()).CreateAsync(
                new(new(Guid.Parse(profileId)), variant));

        Assert.Equal(new NavigationBounds(new(0, height, 0), new(4, height, 2)), restored.Bounds);
        Assert.Equal(new NavigationPortal(1, new(2, height, 2), new(2, height, 0), 0.75f, 1, 12), restored.Portals.Span[0]);
        Assert.Equal(new NavigationPolygon(0, 4, 0, 1, 3, 4, 0, 4), restored.Polygons.Span[0]);
        Assert.Equal(new Vector3(1, height + 0.25f, 1), restored.DetailPositions.Span[6]);
        Assert.Equal(new NavigationDetailTriangle(2, 3, 6), restored.DetailTriangles.Span[2]);
        Assert.Equal(9UL, restored.ContentVersion);
    }

    private static NavigationMesh CreateMesh(float height) => new(
        [new(0, height, 0), new(0, height, 2), new(2, height, 2), new(2, height, 0), new(4, height, 2), new(4, height, 0)],
        [0, 1, 2, 3, 3, 2, 4, 5],
        [new(0, 4, 0, 1, 3, 4, 0, 4), new(4, 4, 1, 1, 7, 2, 4, 2, new(1, 0))],
        [new(1, new(2, height, 2), new(2, height, 0), 0.75f, 1, 12), new(0, new(2, height, 0), new(2, height, 2), 0.25f, 1)],
        [new(0, height, 0), new(0, height, 2), new(2, height, 2), new(2, height, 0), new(4, height, 2), new(4, height, 0), new(1, height + 0.25f, 1)],
        [new(0, 1, 6), new(1, 2, 6), new(2, 3, 6), new(3, 0, 6), new(3, 2, 4), new(3, 4, 5)], contentVersion: 9);
}
