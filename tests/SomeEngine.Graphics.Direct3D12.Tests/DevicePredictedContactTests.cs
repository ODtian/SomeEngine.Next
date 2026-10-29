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
    [Theory]
    [InlineData("Sphere")]
    [InlineData("Box")]
    [InlineData("Hull")]
    public void FastDeviceShapesStayWithinTheThinFloorContactDistanceAcrossFrames(string shape)
    {
        const double h = 1d / 60;
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
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
        BodyMass mass = world.Read<BodyMass>(moving);
        mass.InverseInertia = Matrix4x4.CreateScale(1 / inertia);
        world.Replace(moving, mass);
        world.Replace(moving, new BodyVelocity { Linear = new(0, 0, -30) });
        foreach (var (body, index) in new[] { (floor, 0), (moving, 1) })
        {
            world.Add(body, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = .5f, DynamicFriction = .5f });
            world.Add(body, new DeviceCollisionBody(grid, index));
            world.Add(body, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>());
        QueryHandle shapes = world.Query(new QueryDefinitionBuilder().All<DeviceGeometryBinding>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle states = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        var pairs = world.Within(shapes);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var boxInputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 1, 64);
        using var hullInputs = grid.WriteGeometryReferences<ConvexHullGeometry, DeviceComponentSource<ConvexHullGeometry, DeviceHullReader>>(pairs, default, 2, 64);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceSphereUpdate"), 0, DeviceInputTransfer.Upload);
        using var boxUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceBoxUpdate"), 1, DeviceInputTransfer.Resident);
        using var hullUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileThinFloorGeometry("DeviceHullUpdate"), 2, DeviceInputTransfer.Resident);
        int bin = shape == "Sphere" ? 1 : shape == "Box" ? 2 : 4;
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompileThinFloorDetection(shape), bin,
            new(4, 4, 1) { BodiesPerContact = 2 }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        using var detected = new ContactPointBuffer(jobs, 16, 32);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 16, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 16, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 16, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 16, "DeviceContactResponses");
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection, bodies, anchors,
            CompileContactMatching(), new(.02f, .99f, true), 37, 64);
        using var current = matching.CreateGeometryUpdate(bodies, anchors, CompileCurrentContactGeometry());
        using var refresh = ContactPoints.Create(world, poses, points, detected, new AverageContactMaterials(),
            matching.Components(new ThinFloorContactComponents(histories, responses)), new(.02f, .99f, true));
        using var input = new DeviceContactInputs(world, points, parameters, anchors);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(16, 2), bindings, reads, 64, new[] { selected });
        JobResourceAccess[] solveAccesses = [.. reads, histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs)];
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(16, 2, .8f, 0),
            bindings, solveAccesses, 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(), bindings, reads, 64, new[] { selectedResponse });
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var stages = bodyInputs.Upload().Then(dynamics.Predict(new(0, 0, -10)))
            .Then(grid.BeginInputs()).Then(sphereInputs).Then(boxInputs).Then(hullInputs)
            .Then(sphereUpdate.Update()).Then(boxUpdate.Update()).Then(hullUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(new ClearThinFloorContacts(detected)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess }))
            .Then(matching.Match()).Then(refresh.Refresh(current.Results)).Then(input.Upload()).Then(stateInput.Upload())
            .Then(current.Update()).Then(response.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities());
        var initialize = new PhysicsStepContext(world, QueryDefinition.Empty, 17, 0, 1, 10, h);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1)
            .CreateFrame(results.Readback(64).Then(contactResults.Readback(64)), h, 0, 17, 10);
        frame.AdvanceTime(h * 1.5);
        PhysicsStepResult first = frame.CompleteForRead();
        BodyPose pose = world.Read<BodyPose>(moving);
        BodyStepState step = world.Read<BodyStepState>(moving);
        var contacts = new List<Entity>();
        world.ExecuteReadQuery(points, rows => { foreach (QueryRow row in rows.Rows) contacts.Add(row.Entity); });
        CollisionBounds firstBounds = GetThinFloorBounds(world, moving, shape, in pose);
        Assert.InRange(firstBounds.Min.Z, -.02f, .02f);
        Assert.Equal(17, first.Number);
        Assert.Equal(17, step.Number);
        Assert.InRange(Math.Abs(first.EndTime - (10 + h)), 0, 1e-12);
        Assert.NotEmpty(contacts);
        Assert.All(contacts, point =>
        {
            Assert.True(world.Read<ContactPoint>(point).Prediction.Enabled);
            Assert.Equal(10, world.Read<ContactPoint>(point).Prediction.SampleTime);
            Assert.Equal(10, world.Read<ContactResponse>(point).StartTime);
            Assert.InRange(Math.Abs(world.Read<ContactResponse>(point).EndTime - (10 + h)), 0, 1e-12);
        });
        frame.AdvanceTime(h);
        PhysicsStepResult second = frame.CompleteForRead();
        BodyPose secondPose = world.Read<BodyPose>(moving);
        CollisionBounds secondBounds = GetThinFloorBounds(world, moving, shape, in secondPose);
        Assert.InRange(secondBounds.Min.Z, -.02f, .02f);
        Assert.Equal(18, second.Number);
        world.ReleaseQuery(states); world.ReleaseQuery(points); world.ReleaseQuery(poses);
        world.ReleaseQuery(shapes); world.ReleaseQuery(members);
    }

    private static CollisionBounds GetThinFloorBounds(World world, Entity moving, string shape, in BodyPose pose)
    {
        if (shape == "Sphere")
        {
            SphereGeometry geometry = world.Read<SphereGeometry>(moving);
            return SphereGeometry.Bounds(in geometry, in pose);
        }
        if (shape == "Box")
        {
            BoxGeometry geometry = world.Read<BoxGeometry>(moving);
            return BoxGeometry.Bounds(in geometry, in pose);
        }
        ConvexHullGeometry hull = world.Read<ConvexHullGeometry>(moving);
        return ConvexHullGeometry.Bounds(in hull, in pose);
    }

    private readonly struct ClearThinFloorContacts(ContactPointBuffer contacts) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        { dependency.Complete(); contacts.Clear(); return default; }
    }

    private readonly struct ThinFloorContactComponents(DeviceConstraintState<DeviceAvbdContactState> histories,
        DeviceConstraintState<DeviceContactResponseState> responses) : IContactPointComponents
    {
        public void AddReads(World world, List<JobResourceAccess> accesses) { }
        public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
            ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
        {
            float penalty = 1 / (float)(context.Duration * context.Duration);
            commands.Add(point, new AvbdContactState { NormalPenalty = penalty, TangentPenalty = penalty });
            commands.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
            commands.Add(point, new DeviceContactResponseBinding(responses));
        }
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
        Vector3[] vertices = [new(.2f,0,0),new(-.2f,0,0),new(0,.2f,0),new(0,-.2f,0),new(0,0,.2f),new(0,0,-.2f)];
        var faces = new HullFace[8]; var indices = new int[24]; int face = 0;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        for (int z = -1; z <= 1; z += 2)
        {
            int first = x > 0 ? 0 : 1, second = y > 0 ? 2 : 3, third = z > 0 ? 4 : 5;
            if (x * y * z < 0) (second, third) = (third, second);
            indices[face * 3] = first; indices[face * 3 + 1] = second; indices[face * 3 + 2] = third;
            faces[face] = new(Vector3.Normalize(new(x,y,z)), face * 3, 3); face++;
        }
        return new(vertices, faces, indices);
    }
}
