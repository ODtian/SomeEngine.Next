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
    public void DeviceEdgeSamplesOnTheSameBodyFeedTheirDistinctAnchorsToTheCommonResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var fields = new DeviceSdfFields(backend, device, new(1, 8));
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity body = AddBody(world, bodies, 0, new Vector3(.1f, 0, 0));
        world.Replace(body, new BodyPose { Position = new(.1f, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        Entity edge = world.CreateEntity(new DynamicEdge(new(body, -Vector3.UnitX), new(body, Vector3.UnitX)));
        world.Add(edge, new DeviceCollisionBody(grid, 0));
        world.Add(edge, new DeviceGeometryBinding(geometry, 0));
        Entity field = AddBody(world, bodies, 1, Vector3.Zero);
        world.Replace(field, new BodyMass { Motion = BodyMotion.Static, InverseInertia = Matrix4x4.Identity });
        var data = new SdfGridData(2, 2, 2, new float[] { -1.25f, .75f, -1.25f, .75f, -1.25f, .75f, -1.25f, .75f });
        world.Add(field, new SdfGeometry<TrilinearSdfGrid>(new(data, new(new Vector3(-1), new Vector3(2))), 1, .8f));
        world.Add(field, new DeviceSdfBinding(fields, 0));
        world.Add(field, new DeviceCollisionBody(grid, 1));
        world.Add(field, new DeviceGeometryBinding(geometry, 1));
        world.Add(edge, new ContactMaterial { NormalStiffness = 100 });
        world.Add(field, new ContactMaterial { NormalStiffness = 100 });
        world.Add(body, new AvbdBodyEquation());
        world.Add(field, new AvbdBodyEquation());
        QueryHandle edges = world.Query(world.QueryDefinition().All<DynamicEdge>());
        QueryHandle fieldMembers = world.Query(world.QueryDefinition().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Between(edges, fieldMembers);
        using var edgeInputs = grid.WriteGeometryReferences<DynamicEdge, DeviceDynamicEdgeSource>(pairs, default, 0, 64);
        using var fieldInputs = grid.WriteGeometryReferences<SdfGeometry<TrilinearSdfGrid>, DeviceSdfSource<TrilinearSdfGrid, DeviceTrilinearSdfReader>>(
            pairs, default, 1, 64);
        using var edgeUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompileUpdate("DeviceDynamicCapsuleUpdate"), 0, DeviceInputTransfer.Upload);
        using var fieldUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            Compile("edge_field_bounds", """
                #include "physics_sdf_update.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<DeviceSdfUpdate>(id.x); }
                """), 1, DeviceInputTransfer.Resident, fields.GetBindings(), fields.GetReadAccesses(jobs));
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            Compile("edge_field", """
                #include "physics_sdf_collision.slang"
                #include "physics_sdf_grid.slang"
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) {
                    DeviceSdfCollision<DeviceCapsule, DeviceTrilinearSdfGrid> kernel;
                    kernel.settings.cells = uint3(2,2,2); kernel.settings.rootIterations = 20;
                    kernel.settings.supportIterations = 8; kernel.settings.tolerance = 1e-5f;
                    detectDeviceReferencedCollision(id.x, kernel);
                }
                """), 1, new(4, 4, 1), new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident,
            DeviceCollisionReadback.Contacts, fields.GetBindings(), fields.GetReadAccesses(jobs));
        using var detected = new ContactPointBuffer(jobs, 4, 12);
        QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
        QueryHandle pointScope = world.Query(world.QueryDefinition().All<ContactPoint>());
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState
            { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f }), new ContactRefreshSettings(.02f, .99f, false));
        QueryHandle responseBodies = world.Query(world.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().Read<BodyVelocity>().ReadWrite<AvbdBodyEquation>());
        QueryHandle responsePoints = world.Query(world.QueryDefinition().Read<ContactPoint>()
            .Read<UndirectedRelationEndpoints<ContactPoint>>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>()
            .Optional<DirectedRelationEndpoints<ContactParticipant>>(QueryAccess.Read).ReadWrite<AvbdContactState>().ReadWrite<ContactResponse>());
        var stages = new PredictBodies(Vector3.Zero, 64).Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs())
            .Then(grid.BeginInputs()).Then(edgeInputs).Then(fieldInputs).Then(fields.UploadInputs())
            .Then(edgeUpdate.Update()).Then(fieldUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess })).Then(points.Refresh())
            .Then(new InitializeContactResponses(world, responseBodies, responsePoints))
            .Then(SolveAvbd.Create(world, responseBodies, new AvbdConstraints<ContactPoint, AvbdContactMath>(responsePoints), 32, 0, 0))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(6, 2, .25).Complete();

        Assert.InRange(world.Read<BodyPose>(body).Position.X, .235f, .25f);
        Assert.True(world.Read<BodyVelocity>(body).Linear.X > 0);
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        Assert.NotEmpty(contacts);
        for (int point = 0; point < contacts.Length; point++)
        {
            DeviceContactBody[] ends = detection.ReadContactBodies(0, point).ToArray();
            Assert.Equal(2, contacts[point].FirstBodyCount);
            Assert.Equal(body, ends[0].Body.Body);
            Assert.Equal(body, ends[1].Body.Body);
            Assert.Equal(-Vector3.UnitX, ends[0].Body.LocalPoint);
            Assert.Equal(Vector3.UnitX, ends[1].Body.LocalPoint);
            Near(1, ends[0].Body.Weight + ends[1].Body.Weight);
            Assert.Equal(field, ends[2].Body.Body);
        }
        world.ReleaseQuery(responsePoints);
        world.ReleaseQuery(responseBodies);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(edges);
        world.ReleaseQuery(fieldMembers);
    }
}
