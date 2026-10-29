using System.Numerics;
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

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void PredictingTheSeparatedSiloBoxesKeepsTheirContactsOnTheirSampledFaces()
    {
        const double h = 1d / 60;
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(), new(.5f, 4), new(2, 256, 257, 4, 1));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        var firstPose = new BodyPose
        {
            Position = new(-8.274739f, 3.6800156f, 5.733334f),
            Rotation = new(-1.504644e-6f, -3.6044827e-7f, -.84806687f, -.52988905f),
        };
        var secondPose = new BodyPose
        {
            Position = new(-7.777169f, 4.0455575f, 5.733352f),
            Rotation = new(8.852145e-6f, 7.812386e-6f, .6169662f, .7869896f),
        };
        Entity first = CreateBody(world, bodies, equations, 0, firstPose.Position, BodyMotion.Dynamic);
        Entity second = CreateBody(world, bodies, equations, 1, secondPose.Position, BodyMotion.Dynamic);
        world.Replace(first, firstPose); world.Replace(second, secondPose);
        world.Replace(first, new BodyStepState
        {
            StartCenterOfMass = firstPose.Position, StartRotation = firstPose.Rotation,
            PredictedCenterOfMass = new(-8.273794f, 3.6800184f, 5.622223f),
            PredictedRotation = new(-1.7734084e-6f, -4.2478885e-7f, -.84723353f, -.5312207f),
            Number = 39, Substep = 0, Time = .65 + h,
        });
        world.Replace(second, new BodyStepState
        {
            StartCenterOfMass = secondPose.Position, StartRotation = secondPose.Rotation,
            PredictedCenterOfMass = new(-7.7765813f, 4.04594f, 5.6222415f),
            PredictedRotation = new(9.07699e-6f, 8.540063e-6f, .61708343f, .7868976f),
            Number = 39, Substep = 0, Time = .65 + h,
        });
        foreach (var (body, index) in new[] { (first, 0), (second, 1) })
        {
            world.Add(body, new BoxGeometry(new(.2f)));
            world.Add(body, new DeviceCollisionBody(grid, index));
            world.Add(body, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle shapes = world.Query(new QueryDefinitionBuilder().All<DeviceGeometryBinding>());
        var pairs = world.Within(shapes);
        using var inputs = new DeviceBodyInputs(world, members, bodies);
        using var references = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 0, 64);
        using var update = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompilePoseGeometryUpdate("DeviceBoxUpdate"), 0, DeviceInputTransfer.Upload);
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("silo_box_face_clipping", """
            #include "physics_collision.slang"
            #include "physics_collision_prediction.slang"
            struct SiloBoxPrediction : IDeviceCollisionPrediction
            {
                DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
                { DeviceConvexCollision<DeviceBox, DeviceBox> shape; return shape.distance(work, first, second); }
                uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
                    DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
                { DeviceConvexCollision<DeviceBox, DeviceBox> shape; return shape.manifold(work, first, second, distance, points); }
                override float4 getSampleRotation(DeviceGeometryInput geometry, DeviceBodyState body)
                { return body.predictedRotation; }
                DeviceFeaturePoint getSupportPoint(DeviceGeometryInput geometry, float3 direction)
                { DeviceBox shape; return shape.support(geometry, direction); }
                uint getPredictedManifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
                    DeviceGeometryDistance distance, float manifoldDistance, out DeviceGeometryDistance points[4])
                { DeviceConvexCollision<DeviceBox, DeviceBox> shape; return shape.manifold(work, first, second, distance, manifoldDistance, points); }
            };
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id:SV_DispatchThreadID)
            { SiloBoxPrediction shape; predictDeviceRigidCollision(id.x, shape); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, Entry(shader, 0), 0,
            new(4, 4, 1) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        var stages = inputs.Upload().Then(grid.BeginInputs()).Then(references).Then(update.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect());
        var context = new PhysicsStepContext(world, QueryDefinition.Empty, 39, 0, 1, .65, h);

        stages.Schedule(in context, default).Complete();

        Assert.Single(detection.ReadDistances().ToArray());
        CollisionContact[] contacts = detection.ReadContacts(0).ToArray();
        foreach (CollisionContact contact in contacts)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.InRange(contact.FirstLocalPoint[axis], -.2001f, .2001f);
                Assert.InRange(contact.SecondLocalPoint[axis], -.2001f, .2001f);
            }
            Assert.InRange(contact.Separation, -.02f, .02f);
        }
        Assert.Empty(contacts);
        world.ReleaseQuery(shapes); world.ReleaseQuery(members);
    }
}