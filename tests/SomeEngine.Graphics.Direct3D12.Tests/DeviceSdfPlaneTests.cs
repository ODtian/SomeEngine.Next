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

public sealed partial class DeviceReferencedGeometryTests
{
    [Fact]
    public void DevicePlaneProductRestoresTheFieldWitnessesAndNormalToItsOriginalFirstPosition()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var fields = new DeviceSdfFields(backend, device, new(1, 8));
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity plane = AddBody(world, bodies, 0, Vector3.Zero);
        world.Add(plane, new PlaneGeometry(Vector3.UnitY, 0));
        world.Add(plane, new DeviceCollisionBody(grid, 0));
        world.Add(plane, new DeviceGeometryBinding(geometry, 0));
        Entity field = AddBody(world, bodies, 1, Vector3.Zero);
        var data = new SdfGridData(2, 2, 2, new float[] { -.75f, -.75f, 1.25f, 1.25f, -.75f, -.75f, 1.25f, 1.25f });
        world.Add(field, new SdfGeometry<TrilinearSdfGrid>(new(data, new(new Vector3(-1), new Vector3(2))), 1, .8f));
        world.Add(field, new DeviceSdfBinding(fields, 0));
        world.Add(field, new DeviceCollisionBody(grid, 1));
        world.Add(field, new DeviceGeometryBinding(geometry, 1));
        QueryHandle planes = world.Query(world.QueryDefinition().All<PlaneGeometry>());
        QueryHandle fieldMembers = world.Query(world.QueryDefinition().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Product(planes, fieldMembers);
        var planeSource = new DeviceComponentSource<PlaneGeometry, DevicePlaneReader>(
            new DevicePlaneReader(new CollisionBounds(new Vector3(-2), new Vector3(2))));
        using var planeInputs = grid.WriteGeometryReferences<PlaneGeometry, DeviceComponentSource<PlaneGeometry, DevicePlaneReader>>(
            pairs, planeSource, 0, 64);
        using var fieldInputs = grid.WriteGeometryReferences<SdfGeometry<TrilinearSdfGrid>, DeviceSdfSource<TrilinearSdfGrid, DeviceTrilinearSdfReader>>(
            pairs, default, 1, 64);
        using var planeUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DevicePlaneUpdate"), 0, DeviceInputTransfer.Upload);
        using var fieldUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            Compile("plane_field_bounds", """
                #include "physics_sdf_update.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<DeviceSdfUpdate>(id.x); }
                """), 1, DeviceInputTransfer.Resident, fields.GetBindings(), fields.GetReadAccesses(jobs));
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            Compile("plane_field", """
                #include "physics_sdf_collision.slang"
                #include "physics_sdf_grid.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) {
                    DeviceReverseCollision<DeviceSdfPlaneCollision<DeviceTrilinearSdfGrid>> kernel;
                    kernel.kernel.sampling.cells = uint3(2,2,2);
                    kernel.kernel.sampling.rootIterations = 20;
                    kernel.kernel.sampling.tolerance = 1e-5f;
                    kernel.kernel.tolerance = 1e-5f;
                    detectDeviceReferencedCollision(id.x, kernel);
                }
                """), 1, new(4, 4, 1), new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident,
            DeviceCollisionReadback.Contacts, fields.GetBindings(), fields.GetReadAccesses(jobs));
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(grid.BeginInputs()).Then(planeInputs).Then(fieldInputs)
            .Then(fields.UploadInputs()).Then(planeUpdate.Update()).Then(fieldUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect());

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(3, 1, .25).Complete();

        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.Equal(4, contacts.Length);
        Assert.All(contacts, contact =>
        {
            Assert.Equal(plane, contact.First);
            Assert.Equal(field, contact.Second);
            Near(Vector3.UnitY, contact.Normal);
            Near(-.25f, contact.Separation);
            Near(0, contact.FirstLocalPoint.Y);
            Near(-.25f, contact.SecondLocalPoint.Y);
            Near(-.125f, contact.WorldPoint.Y);
        });
        world.ReleaseQuery(planes);
        world.ReleaseQuery(fieldMembers);
    }
}
