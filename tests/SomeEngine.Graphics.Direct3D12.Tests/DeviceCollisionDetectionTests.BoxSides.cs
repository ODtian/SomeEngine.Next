using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
    public void TouchingDeviceBoxesKeepTheirSideContactAtThePyramidArrayCoordinates()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 16), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0));
        Entity first = world.CreateEntity(new BoxGeometry(new(.5f)));
        world.Add(first, new BodyPose
        {
            Position = new(-229.49994f, -53.999992f, 15.494312f),
            Rotation = new(-2.0194131e-5f, 1.1930933e-5f, -9.0576505e-6f, 1),
        });
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(first, new DeviceGeometryBinding(geometry, 0));
        Entity second = world.CreateEntity(new BoxGeometry(new(.5f)));
        world.Add(second, new BodyPose
        {
            Position = new(-228.49994f, -54, 15.494275f),
            Rotation = new(-1.0873484e-5f, 1.927164e-6f, -6.002229e-6f, 1),
        });
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(second, new DeviceGeometryBinding(geometry, 1));
        QueryDefinition bodies = new QueryDefinitionBuilder().All<BoxGeometry>().Build();
        QueryHandle members = world.Query(bodies);
        var pairs = world.Within(members);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"), 0, new(4, 4, 1), new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Upload, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 0, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect());

        PhysicsSimulation.Create(world, bodies, stages, 1).Schedule(0, 0, 1d / 60).Complete();

        CollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray()).Distance;
        Assert.True(Vector3.Distance(Vector3.UnitX, distance.Normal) <= 1e-3f,
            $"Normal={distance.Normal}; Separation={distance.Separation}; First={distance.FirstPoint}; Second={distance.SecondPoint}");
        Assert.InRange(distance.Separation, -.0001f, .0001f);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        foreach (CollisionContact contact in contacts)
        {
            Assert.InRange(Vector3.Distance(Vector3.UnitX, contact.Normal), 0, 1e-3f);
            Assert.InRange(contact.Separation, -.0001f, .0001f);
        }
        world.ReleaseQuery(members);
    }
}
