using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class AsaNavigationTests
{
    [Fact]
    public void LongActorTurnsInTheWideRegionAndArrivesThroughTheAlignedExit()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        NavigationProfile profile = Profile();
        var policy = new NavigationPolicy();
        HeadingNavigationMesh mesh = HeadingNavigationMeshBuilder.Schedule(Scene(), profile, policy,
            new() { ChannelCount = 16 }, scheduler: scheduler).Complete().Mesh;
        var navigation = new NavigationSystems<HeadingNavigationMesh, AsaNavigationSearch, AsaSearchState, PoseMotion, PoseReader>(world);
        Entity actor = world.CreateEntity(new PoseMotion(new(3, 0, 2), default, 0));
        Vector3 goal = new(9, 0, 7);
        navigation.AddAgent(actor, mesh, profile, policy, new(goal, 0.05f, MathF.PI / 2, 0.01f), 2);
        navigation.Schedule(0.1f, 1).Steering.Complete();
        Assert.Equal(NavigationSearchStatus.Searching, world.Read<NavigationPathResult>(actor).Status);
        var poses = new List<NavigationPose>();
        for (int frame = 0; frame < 600 && world.Read<NavigationProgress>(actor).Status != NavigationStatus.Arrived; frame++)
        {
            navigation.Schedule(0.1f, 1024).Steering.Complete();
            PoseMotion before = world.Read<PoseMotion>(actor);
            NavigationSteering steering = world.Read<NavigationSteering>(actor);
            float turn = Math.Clamp(NavigationHeading.Difference(steering.Heading, before.Heading),
                -profile.MaximumAngularSpeed * 0.1f, profile.MaximumAngularSpeed * 0.1f);
            var after = new PoseMotion(before.Position + steering.Velocity * 0.1f, steering.Velocity, before.Heading + turn);
            world.Replace(actor, after);
            navigation.ScheduleProgress(default).Complete();
            poses.Add(new(after.Position, after.Heading));
        }

        NavigationPath path = world.Read<NavigationPathResult>(actor).Path!;
        Assert.NotNull(path);
        Assert.True(path.Poses.Length > 2);
        Assert.Equal(NavigationStatus.Arrived, world.Read<NavigationProgress>(actor).Status);
        Assert.All(poses, pose => Assert.True(mesh.IsPoseFeasible(pose.Position, pose.Heading), $"{pose}"));
        Assert.Contains(poses, pose => pose.Position.Z > 4 && pose.Position.X < 6 && pose.Heading > 0.3f && pose.Heading < 1.3f);
        Assert.All(poses.Where(pose => pose.Position.X > 6), pose => Assert.InRange(
            MathF.Abs(NavigationHeading.Difference(MathF.PI / 2, pose.Heading)), 0, MathF.PI / 8));
        Assert.InRange(MathF.Abs(NavigationHeading.Difference(MathF.PI / 2, world.Read<PoseMotion>(actor).Heading)), 0, 0.01f);
        Assert.True(path.Corners.Length >= path.Corridor.Length + 1);
    }

    internal static NavigationProfile Profile() => new(
        [new(-0.2f, -1.2f), new(0.2f, -1.2f), new(0.3f, 0.7f), new(0, 1.2f), new(-0.3f, 0.7f)], 1.5f);

    // This L-shaped pair of passages is one example of geometry with changing feasible headings.
    internal static NavigationBuildInput Scene()
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

    internal readonly record struct PoseMotion(Vector3 Position, Vector3 Velocity, float Heading) : IComponent;
    internal readonly struct PoseReader : INavigationMotionReader<PoseMotion>
    {
        public static NavigationMotion Read(in PoseMotion motion) => new(motion.Position, motion.Velocity, motion.Heading, true);
    }
}
