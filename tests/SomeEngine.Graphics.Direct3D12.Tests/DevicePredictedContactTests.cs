using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Theory]
    [InlineData("Sphere")]
    [InlineData("Box")]
    [InlineData("Hull")]
    public void FastDeviceShapesStayWithinTheThinFloorContactDistanceAcrossFrames(string shape)
    {
        const double h = 1d / 60;
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(), new(.5f, 4), new(2, 256, 257, 4, 6));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 6, 8, 24), bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, new(0, 0, -.1f), BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, new(0, 0, .3f), BodyMotion.Dynamic);
        world.Add(floor, new BoxGeometry(new(2, 2, .1f)));
        if (shape == "Sphere") world.Add(moving, new SphereGeometry(.2f));
        else if (shape == "Box") world.Add(moving, new BoxGeometry(new(.2f)));
        else world.Add(moving, new ConvexHullGeometry(CreateThinFloorHull(), 0));
        float inertia = shape == "Sphere" ? .016f : shape == "Box" ? .026666667f : .008f;
        BodyMass mass = world.ReadComponentData<BodyMass>(moving);
        mass.InverseInertia = Matrix4x4.CreateScale(1 / inertia);
        world.Set(moving, mass);
        world.Set(moving, new BodyVelocity { Linear = new(0, 0, -30) });
        foreach (var (body, index) in new[] { (floor, 0), (moving, 1) })
        {
            world.Add(body, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = .5f, DynamicFriction = .5f });
            world.Add(body, new DeviceCollisionBody(grid, index));
            world.Add(body, new DeviceGeometryBinding(geometry, index));
        }
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query shapes = world.Query(new QueryDescription().With<DeviceGeometryBinding>());
        var pairs = world.Within(shapes);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var boxInputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 1, 64);
        using var hullInputs = grid.WriteGeometryReferences<ConvexHullGeometry, DeviceComponentSource<ConvexHullGeometry, DeviceHullReader>>(pairs, default, 2, 64);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceSphereUpdate"), 0, DeviceInputTransfer.Upload);
        using var boxUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceBoxUpdate"), 1, DeviceInputTransfer.Resident);
        using var hullUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceHullUpdate"), 2, DeviceInputTransfer.Resident);
        int bin = shape == "Sphere" ? 1 : shape == "Box" ? 2 : 4;
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileThinFloorDetection(shape), bin,
            new(4, 4, 1) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.None);
        float penalty = 1 / (float)(h * h);
        using var manifolds = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(4, 16, 32),
            new() { NormalPenalty = penalty, TangentPenalty = penalty });
        using var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, manifolds, detection, bodies,
            CompileContactMatching(), new(.02f, .99f, true), new() { NormalStiffness = float.PositiveInfinity, StaticFriction = .5f, DynamicFriction = .5f }, 37);
        using var stateInput = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, CompileHistoryStateInitialization());
        var bindings = new Dictionary<string, ResourceBinding[]>(); manifolds.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(16, 2), bindings, reads, 64, contacts: manifolds);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(16, 2, .8f, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, manifolds, CompileContactActions(), bindings, reads, 64,
            Array.Empty<IDeviceConstraintSelection>());
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var stages = bodyInputs.Upload().Then(dynamics.Predict(new(0, 0, -10)))
            .Then(grid.BeginInputs()).Then(sphereInputs).Then(boxInputs).Then(hullInputs)
            .Then(sphereUpdate.Update()).Then(boxUpdate.Update()).Then(hullUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect())
            .Then(matching.Match()).Then(stateInput.Initialize()).Then(response.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities());
        using var frame = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1)
            .CreateFrame(results.Readback(64).Then(contactResults.Readback(manifolds)), h, 0, 17, 10);
        frame.AdvanceTime(h * 1.5);
        PhysicsStepResult first = frame.CompleteForRead();
        BodyPose pose = world.ReadComponentData<BodyPose>(moving);
        BodyStepState step = world.ReadComponentData<BodyStepState>(moving);
        ContactView view = contactResults.Contacts.OpenView();
        CollisionBounds firstBounds = GetThinFloorBounds(world, moving, shape, in pose);
        Assert.InRange(firstBounds.Min.Z, -.02f, .02f);
        Assert.Equal(17, first.Number);
        Assert.Equal(17, step.Number);
        Assert.InRange(Math.Abs(first.EndTime - (10 + h)), 0, 1e-12);
        Assert.True(view.PointCount > 0);
        for (int point = 0; point < view.PointCount; point++)
        {
            Assert.True(view.GetPoint(point).Prediction.Enabled);
            Assert.Equal(10, view.GetPoint(point).Prediction.SampleTime);
            Assert.Equal(10, view.Responses[point].StartTime);
            Assert.InRange(Math.Abs(view.Responses[point].EndTime - (10 + h)), 0, 1e-12);
        }
        frame.AdvanceTime(h);
        PhysicsStepResult second = frame.CompleteForRead();
        BodyPose secondPose = world.ReadComponentData<BodyPose>(moving);
        CollisionBounds secondBounds = GetThinFloorBounds(world, moving, shape, in secondPose);
        Assert.InRange(secondBounds.Min.Z, -.02f, .02f);
        Assert.Equal(18, second.Number);
    }

    private static CollisionBounds GetThinFloorBounds(World world, Entity moving, string shape, in BodyPose pose)
    {
        if (shape == "Sphere")
        {
            SphereGeometry geometry = world.ReadComponentData<SphereGeometry>(moving);
            return SphereGeometry.Bounds(in geometry, in pose);
        }
        if (shape == "Box")
        {
            BoxGeometry geometry = world.ReadComponentData<BoxGeometry>(moving);
            return BoxGeometry.Bounds(in geometry, in pose);
        }
        ConvexHullGeometry hull = world.ReadComponentData<ConvexHullGeometry>(moving);
        return ConvexHullGeometry.Bounds(in hull, in pose);
    }

    private ComputePipelineDesc CompileThinFloorGeometry(string type)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("thin_floor_geometry_" + type, $$"""
            #include "physics_hull_update.slang"
            struct ThinFloorGeometry : IDeviceGeometryUpdate
            {
                DeviceGeometryBounds update(uint index,inout DeviceGeometryInput geometry)
                { {{type}} shape; DeviceGeometryBounds bounds=shape.update(index,geometry); bounds.min-=.02; bounds.max+=.02; return bounds; }
            };
            [shader("compute")][numthreads(64,1,1)]
            void Update(uint3 id:SV_DispatchThreadID) { updateDeviceGeometry<ThinFloorGeometry>(id.x); }
            """, [new("Update", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }

    private ComputePipelineDesc CompileThinFloorDetection(string shape)
    {
        string first = shape == "Sphere" ? "DeviceSphere" : "DeviceBox";
        string second = shape == "Hull" ? "DeviceHull" : "DeviceBox";
        int firstType = shape == "Sphere" ? 0 : 1;
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("thin_floor_detection_" + shape, $$"""
            #include "physics_collision_hull.slang"
            #include "physics_collision_prediction.slang"
            struct ThinFloorCollision : IDeviceCollisionPrediction
            {
                DeviceGeometryDistance distance(uint work,DeviceGeometryInput first,DeviceGeometryInput second)
                { if(first.geometryType=={{firstType}}) { DeviceConvexCollision<{{first}},{{second}}> shape; return shape.distance(work,first,second); }
                  DeviceConvexCollision<{{second}},{{first}}> shape; return shape.distance(work,first,second); }
                uint manifold(uint work,DeviceGeometryInput first,DeviceGeometryInput second,DeviceGeometryDistance distance,out DeviceGeometryDistance points[4])
                { if(first.geometryType=={{firstType}}) { DeviceConvexCollision<{{first}},{{second}}> shape; return shape.manifold(work,first,second,distance,points); }
                  DeviceConvexCollision<{{second}},{{first}}> shape; return shape.manifold(work,first,second,distance,points); }
                DeviceFeaturePoint getSupportPoint(DeviceGeometryInput geometry,float3 unitDirection)
                { if(geometry.geometryType=={{firstType}}) { {{first}} shape; DeviceFeaturePoint point=shape.support(geometry,unitDirection); point.point+=unitDirection*shape.radius(geometry); return point; }
                  {{second}} shape; DeviceFeaturePoint point=shape.support(geometry,unitDirection); point.point+=unitDirection*shape.radius(geometry); return point; }
                uint getPredictedManifold(uint work,DeviceGeometryInput first,DeviceGeometryInput second,DeviceGeometryDistance distance,float manifoldDistance,out DeviceGeometryDistance points[4])
                { if(first.geometryType=={{firstType}}) { DeviceConvexCollision<{{first}},{{second}}> shape; return shape.manifold(work,first,second,distance,manifoldDistance,points); }
                  DeviceConvexCollision<{{second}},{{first}}> shape; return shape.manifold(work,first,second,distance,manifoldDistance,points); }
            };
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id:SV_DispatchThreadID) { ThinFloorCollision shape; predictDeviceRigidCollision(id.x,shape); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }

    private static ConvexHullData CreateThinFloorHull()
    {
        Vector3[] vertices = [new(.2f, 0, 0), new(-.2f, 0, 0), new(0, .2f, 0), new(0, -.2f, 0), new(0, 0, .2f), new(0, 0, -.2f)];
        var faces = new HullFace[8]; var indices = new int[24]; int face = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    int first = x > 0 ? 0 : 1, second = y > 0 ? 2 : 3, third = z > 0 ? 4 : 5;
                    if (x * y * z < 0) (second, third) = (third, second);
                    indices[face * 3] = first; indices[face * 3 + 1] = second; indices[face * 3 + 2] = third;
                    faces[face] = new(Vector3.Normalize(new(x, y, z)), face * 3, 3); face++;
                }
        return new(vertices, faces, indices);
    }
}
