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
    [Theory]
    [InlineData(-5f, -5f, .49722221f, -9.951337e-8f, 7.128587e-8f, -3.222449e-8f)]
    [InlineData(-1.7399466e-7f, 9.4729415e-9f, .4583333f, 4.053291e-9f, -1.8617355e-8f, -2.0969383e-8f)]
    public void ADeviceBoxTouchingAWideFloorKeepsItsVerticalContact(float x, float y, float z,
        float rotationX, float rotationY, float rotationZ)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 16), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0));
        Entity floor = world.CreateEntity(new BoxGeometry(new(1250, 1250, .5f)));
        world.Add(floor, new BodyPose { Position = new(0, 0, -.5f), Rotation = Quaternion.Identity });
        world.Add(floor, new DeviceCollisionBody(grid, 0));
        world.Add(floor, new DeviceGeometryBinding(geometry, 0));
        Entity box = world.CreateEntity(new BoxGeometry(new(.5f)));
        world.Add(box, new BodyPose { Position = new(x, y, z), Rotation = new(rotationX, rotationY, rotationZ, 1) });
        world.Add(box, new DeviceCollisionBody(grid, 1));
        world.Add(box, new DeviceGeometryBinding(geometry, 1));
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
        Assert.InRange(Vector3.Distance(Vector3.UnitZ, distance.Normal), 0, 1e-4f);
        Assert.InRange(distance.Separation, z - .50002f, z - .49998f);
        CollisionContact[] points = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, points.Length);
        foreach (CollisionContact point in points)
        {
            Assert.InRange(Vector3.Distance(Vector3.UnitZ, point.Normal), 0, 1e-4f);
            Assert.InRange(Vector3.Distance(new Vector3(x, y, z), point.WorldPoint), 0, 1);
        }
        world.ReleaseQuery(members);
    }
}
