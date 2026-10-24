using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Tests;

public sealed class NavigationBakeTests
{
    [Fact]
    public void OrdinaryMeshReservesRoomForEveryHeadingOfTheFootprint()
    {
        var profile = new NavigationProfile([new(-1, -0.2f), new(1, -0.2f), new(1, 0.2f), new(-1, 0.2f)],
            1f, maximumStepHeight: 0.5f);
        var policy = new NavigationPolicy();
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        NavigationMesh mesh = NavigationMeshBuilder.Schedule(Scene(), profile, policy, scheduler: scheduler).Complete().Mesh;

        (NavigationStatus status, Vector3[] positions) = Move(mesh, profile, policy, scheduler);

        Assert.Equal(NavigationStatus.Arrived, status);
        Assert.True(positions.Max(position => position.Z) > 5f);
    }

    [Fact]
    public async Task ImportedProfileVariantsKeepTheirPermittedRoutesAfterTheAssetCloses()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-NavigationBake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NavigationProfile[] profiles = [Profile(0.2f, 1f, 0.5f), Profile(0.7f, 2f, 0.2f)];
            var policy = new NavigationPolicy();
            using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
            NavigationMeshSelection[] selections = [new(new(Guid.NewGuid()), "main"), new(new(Guid.NewGuid()), "main")];
            var source = new NavigationMeshImportSource(Scene(),
                [new(selections[0], profiles[0], policy), new(selections[1], profiles[1], policy)], scheduler);
            var services = new ServiceCollection();
            EngineAssetCapabilities.AddAssetCapabilities(services);
            using ServiceProvider provider = services.BuildServiceProvider();
            AssetImporterCapability capability = provider.GetServices<AssetImporterCapability>()
                .Single(value => value.ImporterType == typeof(NavigationMeshImporter));
            string path = Path.Combine(directory, "walkways.navigation.asset");
            await using (var context = new ImportContext(directory, "walkways", new SourceMeta { SourceGuid = SourceGuid.New() }))
            {
                context.EnableImporter<NavigationMeshImportSource, NavigationAsset>(capability.Bind<NavigationMeshImportSource>(provider));
                context.AddAsset(source, new NavigationAsset(context.GetAssetId("walkways")), "walkways", path);
                NavigationAsset asset = await context.GetAssetAsync<NavigationMeshImportSource, NavigationAsset>(source);
                await asset.SaveAsync(path);
            }
            NavigationMesh[] meshes = new NavigationMesh[2];
            using (var reopened = await NavigationAsset.OpenAsync(path))
            {
                NavigationMeshBlock block = await reopened.GetBlockAsync<NavigationMeshBlock>();
                for (int index = 0; index < meshes.Length; index++) meshes[index] = await block.CreateAsync(selections[index]);
            }
            for (int index = 0; index < meshes.Length; index++)
            {
                (NavigationStatus status, Vector3[] positions) = Move(meshes[index], profiles[index], policy, scheduler);
                Assert.Equal(NavigationStatus.Arrived, status);
                Assert.Equal(index == 1, positions.Max(position => position.Z) > 5f);
                Assert.Equal(index == 1 ? 0f : 0.4f, positions.Max(position => position.Y), 2);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0.2f, 1f, 0.5f, false)]
    [InlineData(0.7f, 2f, 0.2f, true)]
    public void BakedMeshGuidesEachProfileAlongItsPermittedRoute(float radius, float height, float stepHeight, bool wideRoute)
    {
        NavigationProfile profile = Profile(radius, height, stepHeight);
        var policy = new NavigationPolicy();
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        NavigationMesh mesh = NavigationMeshBuilder.Schedule(Scene(), profile, policy, scheduler: scheduler).Complete().Mesh;

        (NavigationStatus status, Vector3[] positions) = Move(mesh, profile, policy, scheduler);

        Assert.Equal(NavigationStatus.Arrived, status);
        Assert.Equal(wideRoute, positions.Max(position => position.Z) > 5f);
        Assert.Equal(wideRoute ? 0f : 0.4f, positions.Max(position => position.Y), 2);
    }

    private static NavigationProfile Profile(float radius, float height, float stepHeight) => new(
        [new(-radius, -radius), new(radius, -radius), new(radius, radius), new(-radius, radius)],
        height, maximumStepHeight: stepHeight, maximumSpeed: 2f);

    // These dimensions are a test example of chambers joined by a low, stepped passage and a wider route.
    private static NavigationBuildInput Scene()
    {
        var positions = new List<Vector3>();
        var triangles = new List<NavigationTriangle>();
        void Floor(float minX, float maxX, float minZ, float maxZ, float height, int area = 0)
        {
            int first = positions.Count;
            positions.AddRange([new(minX, height, minZ), new(minX, height, maxZ),
                new(maxX, height, maxZ), new(maxX, height, minZ)]);
            triangles.Add(new(first, first + 1, first + 2, area));
            triangles.Add(new(first, first + 2, first + 3, area));
        }
        Floor(0, 3, 0, 8, 0);
        Floor(9, 12, 0, 8, 0);
        Floor(3, 4, 1.2f, 2.8f, 0);
        Floor(4, 8, 1.2f, 2.8f, 0.4f);
        Floor(8, 9, 1.2f, 2.8f, 0);
        Floor(3, 9, 5, 8, 0);
        Floor(3.2f, 8.8f, 1.2f, 2.8f, 1.6f);
        return new([new(new NavigationGeometry(positions.ToArray(), triangles.ToArray()), Matrix4x4.Identity, 1)],
            new(new(0, -1, 0), new(12, 4, 8)),
            new() { CellSize = 0.2f, CellHeight = 0.05f, TileSize = 16 });
    }

    private static (NavigationStatus Status, Vector3[] Positions) Move(NavigationMesh mesh, NavigationProfile profile, NavigationPolicy policy, Scheduler scheduler)
    {
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
            PolygonSearchState, BakeMotion, BakeMotionReader>(world);
        var entity = world.CreateEntity(new BakeMotion(new(1.5f, 0, 2), default, 0));
        navigation.AddAgent(entity, mesh, profile, policy, new NavigationGoal(position: new(10.5f, 0, 2), arrivalRange: 0.1f), 2f);
        var positions = new List<Vector3>();
        for (int step = 0; step < 180 && world.Read<NavigationProgress>(entity).Status != NavigationStatus.Arrived; step++)
        {
            navigation.Schedule(0.1f, 16).Steering.Complete();
            NavigationSteering steering = world.Read<NavigationSteering>(entity);
            BakeMotion motion = world.Read<BakeMotion>(entity);
            var position = motion.Position + steering.Velocity * 0.1f;
            world.Replace(entity, new BakeMotion(position, steering.Velocity, steering.Heading));
            positions.Add(position);
            navigation.ScheduleProgress(default).Complete();
        }
        return (world.Read<NavigationProgress>(entity).Status, positions.ToArray());
    }

    private readonly record struct BakeMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    private readonly struct BakeMotionReader : INavigationMotionReader<BakeMotion>
    {
        public static NavigationMotion Read(in BakeMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
    }
}
