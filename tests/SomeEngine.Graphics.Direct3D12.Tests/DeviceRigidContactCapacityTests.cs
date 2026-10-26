using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceReferencedGeometryTests
{
    [Fact]
    public void TheSelectedTwoBodyCapacityCarriesAllFourBoxGroundContacts()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(2, 3), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        var ground = AddBody(world, bodies, 0, new(0, 0, -.5f));
        var box = AddBody(world, bodies, 1, new(0, 0, .39722222f));
        world.Replace(ground, new BodyMass { Motion = BodyMotion.Static });
        world.Add(ground, new BoxGeometry(new(30, 30, .5f)));
        world.Add(box, new BoxGeometry(new(1.5f, 1, .4f)));
        foreach (var (entity, index) in new[] { (ground, 0), (box, 1) })
        {
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle boxes = world.Query(new QueryDefinitionBuilder().Read<BoxGeometry>());
        var pairs = world.Within(boxes);
        using var inputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceBoxUpdate"), 0, DeviceInputTransfer.Upload);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            CompileDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"), 0,
            new DeviceDetectionCapacity(4, 4, 1) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(grid.BeginInputs()).Then(inputs)
            .Then(update.Update()).Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect());

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, 1d / 60).Complete();

        Assert.Equal(1024UL, detection.ContactBodies!.Info.Size);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        for (int point = 0; point < contacts.Length; point++)
        {
            Near(Vector3.UnitZ, contacts[point].Normal);
            Assert.InRange(contacts[point].Separation, -.00279f, -.00276f);
            DeviceContactBody[] ends = detection.ReadContactBodies(0, point).ToArray();
            Assert.Equal(2, ends.Length);
            Assert.Equal(ground, ends[0].Body.Body);
            Assert.Equal(box, ends[1].Body.Body);
            Near(1, ends[0].Body.Weight);
            Near(1, ends[1].Body.Weight);
        }
        world.ReleaseQuery(boxes);
    }
}
