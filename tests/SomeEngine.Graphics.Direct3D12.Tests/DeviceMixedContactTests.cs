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
    public void TypedDeviceBinsShareContactEntitiesAndForcesAcrossCompletedSteps()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(),
            new(1, 3), new(4, 256, 257, 16, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(4, 0, 0, 0));
        Entity[] entities =
        [
            CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static),
            CreateBody(world, bodies, equations, 1, new(1.5f, 0, 0), BodyMotion.Dynamic),
            CreateBody(world, bodies, equations, 2, new(3, 0, 0), BodyMotion.Static),
            CreateBody(world, bodies, equations, 3, new(4.75f, 0, 0), BodyMotion.Dynamic),
        ];
        for (int index = 0; index < entities.Length; index++)
        {
            Entity entity = entities[index];
            if (index < 2) world.Add(entity, new SphereGeometry(1));
            else world.Add(entity, new BoxGeometry(Vector3.One));
            world.Add(entity, new ContactMaterial { NormalStiffness = 100 });
            world.Add(entity, new DeviceCollisionBody(grid, index));
            world.Add(entity, new DeviceGeometryBinding(geometry, index));
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().All<DeviceBodyBinding>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        var pairs = world.Within(members);
        ComputePipelineDesc[] kernels =
        [
            CompileMixedDetection("DeviceConvexCollision<DeviceSphere, DeviceSphere>"),
            CompileMixedDetection("DeviceConvexCollision<DeviceSphere, DeviceBox>"),
            CompileMixedDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"),
        ];
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, kernels, 0,
            new(16, 4, 1), new(1e-5f, 32, 64), 0, DeviceInputTransfer.Upload,
            DeviceCollisionReadback.Distances | DeviceCollisionReadback.Contacts);
        using var detected = new ContactPointBuffer(jobs, 32, 64);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 32, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 32, 2);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 32, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 32, "DeviceContactResponses");
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection,
            CompileContactMatching(), new(.25f, .99f, true), bucketCount: 67, threadsPerGroup: 64);
        using var refresh = ContactPoints.Create(world, poses, points, detected, new AverageContactMaterials(),
            matching.Components(new DeviceHistoryComponents(histories, responses)), new(.25f, .99f, true));
        using var input = parameters.WriteInputs<ContactPoint, DeviceContactSource>(world, points, new(anchors), 64,
            [anchors.GetUploadAccess(jobs)]);
        using var stateInput = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, stateRows, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world,
            points, parameters, "DeviceContactsSelection");
        using var selectedState = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponse = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(32, 2),
            bindings, reads, 64, new[] { selected });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0),
            bindings, reads.Concat(new[] { histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { selectedState, selectedResponse });
        using var response = new DeviceContactActions(backend, device, bodies, graph, responses, CompileContactActions(),
            bindings, reads, 64, new[] { selectedResponse });
        var stages = grid.BeginInputs()
            .Then(grid.WriteGeometryInputs<SphereGeometry, DeviceSphereReader>(pairs, default, 0, 64))
            .Then(grid.WriteGeometryInputs<BoxGeometry, DeviceBoxReader>(pairs, default, 1, 64))
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Upload, DevicePairReadback.None))
            .Then(detection.Detect()).Then(detection.WriteContacts(new ContactPointOutput(detected), new JobResourceAccess[] { detected.WriteAccess }))
            .Then(matching.Match()).Then(refresh.Refresh(matching.Results))
            .Then(parameters.BeginInputs()).Then(input).Then(parameters.UploadInputs()).Then(anchors.UploadInputs()).Then(stateInput.Upload())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero)).Then(response.Initialize())
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64)).Then(contactResults.Readback(64));
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);
        var initialize = new PhysicsStepContext(world, QueryDefinition.Empty, 0, 0, 1, 0, .01);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Schedule(in initialize, default).Complete();

        simulation.Schedule(0, 0, .01).Complete();
        var original = new HashSet<Entity>();
        world.ExecuteReadQuery(points, rows => { foreach (QueryRow row in rows.Rows) original.Add(row.Entity); });
        Assert.Equal(3, detection.Count);
        Assert.Equal(6, original.Count);
        Assert.Equal([entities[0], entities[1], entities[2]], detection.ReadDistances().ToArray().Select(value => value.First));
        Assert.Equal([entities[1], entities[2], entities[3]], detection.ReadDistances().ToArray().Select(value => value.Second));
        detected.Clear();
        simulation.Schedule(1, .01, .01).Complete();

        var current = new HashSet<Entity>();
        world.ExecuteReadQuery(points, rows => { foreach (QueryRow row in rows.Rows) current.Add(row.Entity); });
        Assert.True(original.SetEquals(current));
        Assert.All(current, point => Assert.True(world.Read<ContactResponse>(point).ForceOnSecond.Length() > 0));
        Assert.InRange(world.Read<BodyPose>(entities[3]).Position.X, 4.751f, 4.8f);
        Assert.Equal(1, detection.Number);
        Assert.Equal(.02, detection.Time);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(points); world.ReleaseQuery(poses); world.ReleaseQuery(members);
    }

    private ComputePipelineDesc CompileMixedDetection(string type)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("mixed_contact_detection", $$"""
            #include "physics_collision.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id : SV_DispatchThreadID)
            { {{type}} kernel; detectDeviceCollision(id.x,kernel); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
}
