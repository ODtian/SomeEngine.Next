using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class HeadingNavigationMeshTests
{
    [Fact]
    public async Task ReopenedProfileVariantsRetainTheirPoseAndTurningPermissionsAfterTheAssetCloses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-HeadingMesh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            NavigationSurface surface = NavigationSurfaceBuilder.Schedule(Scene(), scheduler: scheduler).Complete().Surface;
            var policy = new NavigationPolicy();
            var small = new NavigationProfile([new(-0.1f, -0.1f), new(0.1f, -0.1f), new(0.1f, 0.1f), new(-0.1f, 0.1f)], 1.5f);
            HeadingNavigationMesh longMesh = HeadingNavigationMeshBuilder.Schedule(surface, Profile(), policy, scheduler: scheduler).Complete().Mesh;
            HeadingNavigationMesh smallMesh = HeadingNavigationMeshBuilder.Schedule(surface, small, policy, scheduler: scheduler).Complete().Mesh;
            var longSelection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            var smallSelection = new NavigationMeshSelection(new(Guid.NewGuid()), "main");
            string path = Path.Combine(directory, "turning.navigation.asset");
            using (var asset = new NavigationAsset())
            using (var block = new HeadingNavigationMeshBlock())
            {
                block.SetMesh(longSelection, longMesh);
                block.SetMesh(smallSelection, smallMesh);
                asset.SetBlock(block);
                await asset.SaveAsync(path);
            }
            using (var asset = await NavigationAsset.OpenAsync(path))
            {
                HeadingNavigationMeshBlock block = await asset.GetBlockAsync<HeadingNavigationMeshBlock>();
                longMesh = await block.CreateAsync(longSelection);
                smallMesh = await block.CreateAsync(smallSelection);
            }

            Vector3 entrance = new(3, 0, 2), turning = new(3, 0, 7);
            Assert.True(longMesh.IsPoseFeasible(entrance, 0));
            Assert.False(longMesh.IsPoseFeasible(entrance, MathF.PI / 2));
            Assert.False(longMesh.IsRotationFeasible(entrance, 0, MathF.PI / 2));
            Assert.True(smallMesh.IsRotationFeasible(entrance, 0, MathF.PI / 2));
            Assert.NotEmpty(longMesh.GetRotationConnections(turning, 0));
            Assert.Contains(longMesh.Graph.Edges.ToArray(), edge => edge.Action == HeadingNavigationAction.Rotation);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LongFootprintUsesAlignedEntrancesAndTurnsInTheWiderRegion()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        HeadingNavigationMesh mesh = HeadingNavigationMeshBuilder.Schedule(Scene(), Profile(), new NavigationPolicy(),
            new() { ChannelCount = 32 }, scheduler: scheduler).Complete().Mesh;
        Vector3 entrance = new(3, 0, 2), turning = new(3, 0, 7), exit = new(9, 0, 7);

        Assert.True(mesh.IsPoseFeasible(entrance, 0));
        Assert.True(mesh.IsPoseFeasible(entrance, MathF.PI));
        Assert.False(mesh.IsPoseFeasible(entrance, MathF.PI / 2));
        Assert.Contains(mesh.GetAllowedHeadings(entrance).ToArray(), interval => interval.Contains(0.04f));
        Assert.True(mesh.IsPoseFeasible(exit, MathF.PI / 2));
        Assert.False(mesh.IsPoseFeasible(exit, 0));
        Assert.NotEmpty(mesh.GetTranslationConnections(entrance, 0));
        Assert.Empty(mesh.GetTranslationConnections(entrance, MathF.PI / 2));
        Assert.True(mesh.IsRotationFeasible(turning, 0, MathF.PI / 2));
        Assert.False(mesh.IsRotationFeasible(entrance, 0, MathF.PI));
        Assert.NotEmpty(mesh.GetRotationConnections(turning, 0));
    }

    private static NavigationProfile Profile() => new(
        [new(-0.2f, -1.2f), new(0.2f, -1.2f), new(0.3f, 0.7f), new(0, 1.2f), new(-0.3f, 0.7f)],
        1.5f, maximumStepHeight: 0.3f);

    // These dimensions and the tapered five-vertex footprint are test examples of the general shape input.
    private static NavigationBuildInput Scene()
    {
        var positions = new List<Vector3>();
        var triangles = new List<NavigationTriangle>();
        void Floor(float minX, float maxX, float minZ, float maxZ)
        {
            int first = positions.Count;
            positions.AddRange([new(minX, 0, minZ), new(minX, 0, maxZ), new(maxX, 0, maxZ), new(maxX, 0, minZ)]);
            triangles.Add(new(first, first + 1, first + 2, 0));
            triangles.Add(new(first, first + 2, first + 3, 0));
        }
        Floor(2, 4, 0, 4);
        Floor(0, 6, 4, 10);
        Floor(6, 12, 6, 8);
        return new([new(new NavigationGeometry(positions.ToArray(), triangles.ToArray()), Matrix4x4.Identity, 1)],
            new(new(0, -1, 0), new(12, 3, 10)), new() { CellSize = 0.2f, CellHeight = 0.05f, TileSize = 32 });
    }
}
