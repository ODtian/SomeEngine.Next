using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceCollisionDetectionTests
{
    [Fact]
    public void ADeviceBoxOnTheLargeGroundKeepsTheUpwardFaceAndItsFourContacts()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(),
            new DeviceGridSettings(2, 3), new DeviceGridCapacity(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new DeviceGeometryCapacity(2, 0, 0, 0));
        var ground = world.CreateEntity(new BoxGeometry(new(30, 30, .5f)));
        world.Add(ground, new BodyPose { Position = new(0, 0, -.5f), Rotation = Quaternion.Identity });
        world.Add(ground, new DeviceCollisionBody(grid, 0)); world.Add(ground, new DeviceGeometryBinding(geometry, 0));
        var box = world.CreateEntity(new BoxGeometry(new(1.5f, 1, .4f)));
        world.Add(box, new BodyPose { Position = new(-1.7401738e-10f, 1.6838351e-28f, .39722222f),
            Rotation = new(-6.550397e-29f, -6.710084e-11f, 2.9714806e-20f, 1) });
        world.Add(box, new DeviceCollisionBody(grid, 1)); world.Add(box, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = world.QueryDefinition().All<BoxGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"), 0,
            new DeviceDetectionCapacity(4, 4, 1), new ConvexCollisionSettings(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs().Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None)).Then(detection.Detect());

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(0, 0, 1.0 / 60).Complete();

        DeviceCollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray());
        Assert.InRange(distance.Distance.Separation, -.00279f, -.00276f);
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, distance.Distance.Normal), 0, 1e-4f);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        Assert.All(contacts, point =>
        {
            Assert.InRange(Vector3.Distance(Vector3.UnitZ, point.Normal), 0, 1e-4f);
            Assert.InRange(point.Separation, -.00279f, -.00276f);
        });
        world.ReleaseQuery(members);
    }
}
