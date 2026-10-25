using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
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
    public void DeviceSdfContactConsumesTheCompletedSampleProducerWithTheSelectedScaleAndBodyFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var fields = new DeviceSdfFields(backend, device, new(1, 8));
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity sphere = AddBody(world, bodies, 0, new Vector3(4, 1, 0));
        world.Add(sphere, new SphereGeometry(.25f));
        world.Add(sphere, new DeviceCollisionBody(grid, 0));
        world.Add(sphere, new DeviceGeometryBinding(geometry, 0));
        Entity field = AddBody(world, bodies, 1, new Vector3(4, 0, 0));
        world.Replace(field, new BodyPose { Position = new(4, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        float[] samples = new float[8];
        var data = new SdfGridData(2, 2, 2, samples);
        world.Add(field, new SdfGeometry<TrilinearSdfGrid>(new(data, new(new Vector3(-1), new Vector3(2))), 2, .8f));
        world.Add(field, new DeviceSdfBinding(fields, 0));
        world.Add(field, new DeviceCollisionBody(grid, 1));
        world.Add(field, new DeviceGeometryBinding(geometry, 1));
        QueryHandle spheres = world.Query(world.QueryDefinition().All<SphereGeometry>());
        QueryHandle fieldMembers = world.Query(world.QueryDefinition().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Between(spheres, fieldMembers);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var fieldInputs = grid.WriteGeometryReferences<SdfGeometry<TrilinearSdfGrid>, DeviceSdfSource<TrilinearSdfGrid, DeviceTrilinearSdfReader>>(pairs, default, 1, 64);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceSphereUpdate"), 0, DeviceInputTransfer.Upload);
        using var fieldUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            Compile("field_update", """
                #include "physics_sdf_update.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<DeviceSdfUpdate>(id.x); }
                """), 1, DeviceInputTransfer.Resident, fields.GetBindings(), fields.GetReadAccesses(jobs));
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            Compile("sphere_field", """
                #include "physics_sdf_collision.slang"
                #include "physics_sdf_grid.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) {
                    DeviceSdfCollision<DeviceSphere, DeviceTrilinearSdfGrid> kernel;
                    kernel.settings.cells = uint3(2,2,2); kernel.settings.rootIterations = 20;
                    kernel.settings.supportIterations = 8; kernel.settings.tolerance = 1e-5f;
                    detectDeviceReferencedCollision(id.x, kernel);
                }
                """), 1, new(4, 4, 1), new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident,
            DeviceCollisionReadback.Contacts | DeviceCollisionReadback.Distances, fields.GetBindings(), fields.GetReadAccesses(jobs));
        var captured = new List<Captured>();
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(grid.BeginInputs())
            .Then(sphereInputs).Then(fieldInputs).Then(fields.UploadInputs()).Then(sphereUpdate.Update()).Then(fieldUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect()).Then(detection.WriteContacts(new Capture(captured),
                new[] { JobResourceAccess.Write(jobs.GetResourceToken(captured)) }));
        var input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobHandle ready = jobs.Schedule(new FillSdfSamples(samples), JobResourceAccess.Write(jobs, samples), jobs.CreateExternalFenceHandle(input.Task));
        PhysicsStepResult step = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(8, 2, .25, ready);

        input.SetResult();
        step.Complete();

        DeviceCollisionDistance distance = Assert.Single(detection.ReadDistances().ToArray());
        Near(-.25f, distance.Distance.Separation);
        Near(-Vector3.UnitY, distance.Distance.Normal);
        Near(new Vector3(4, .75f, 0), distance.Distance.FirstPoint);
        Near(new Vector3(4, 1, 0), distance.Distance.SecondPoint);
        Assert.Contains(captured, value => value.Contact.First == sphere && value.Contact.Second == field);
        Assert.All(captured, value =>
        {
            Assert.Equal(sphere, Assert.Single(value.First).Body);
            Assert.Equal(field, Assert.Single(value.Second).Body);
        });
        Assert.Equal(2.25, detection.Time);
        world.ReleaseQuery(spheres);
        world.ReleaseQuery(fieldMembers);
    }

    private readonly struct FillSdfSamples(float[] samples) : IJob
    {
        public void Execute() => new float[] { -1.5f, .5f, -1.5f, .5f, -1.5f, .5f, -1.5f, .5f }.CopyTo(samples, 0);
    }

}
