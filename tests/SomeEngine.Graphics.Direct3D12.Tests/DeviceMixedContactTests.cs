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
    [Fact]
    public void TypedDeviceBinsShareContactFeaturesAndForcesAcrossCompletedSteps()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(),
            new(1, 3), new(4, 256, 257, 16, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(4, 0, 0, 0), bodies);
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
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        Query shapes = world.Query(new QueryDescription().With<DeviceBodyBinding>());
        var pairs = world.Within(shapes);
        ComputePipelineDesc[] kernels =
        [
            CompileMixedDetection("DeviceConvexCollision<DeviceSphere, DeviceSphere>"),
            CompileMixedDetection("DeviceConvexCollision<DeviceSphere, DeviceBox>"),
            CompileMixedDetection("DeviceConvexCollision<DeviceBox, DeviceBox>"),
        ];
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, kernels, 0,
            new(16, 4, 1) { Contacts = 32, BodiesPerContact = 2 }, new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident,
            DeviceCollisionReadback.Distances);
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(16, 32, 64),
            new() { NormalPenalty = 100, TangentPenalty = 100 });
        using var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, contacts, detection, bodies,
            CompileContactMatching(), new(.25f, .99f, true), new() { NormalStiffness = 100 }, 67);
        using var stateInput = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, CompileHistoryStateInitialization());
        using var sphereInputs = grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(pairs, default, 0, 64);
        using var boxInputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 1, 64);
        using var sphereUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileMixedGeometry("DeviceSphereUpdate"), 0, DeviceInputTransfer.Upload);
        using var boxUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, CompileMixedGeometry("DeviceBoxUpdate"), 1, DeviceInputTransfer.Resident);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(32, 2), bindings, reads, 64, contacts: contacts);
        using var solver = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64,
            Array.Empty<IDeviceConstraintSelection>());
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(grid.BeginInputs()).Then(sphereInputs).Then(boxInputs)
            .Then(sphereUpdate.Update()).Then(boxUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(matching.Match()).Then(stateInput.Initialize()).Then(dynamics.Predict(Vector3.Zero)).Then(response.Initialize())
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64)).Then(contactResults.Readback(contacts));
        var simulation = PhysicsSimulation.Create(world, members, stages, 1);

        simulation.Schedule(0, 0, .01).Complete();
        var original = ContactFeatureKeys(contactResults.Contacts.OpenView());
        Assert.Equal(3, detection.Count);
        Assert.Equal(6, original.Count);
        Assert.Equal([entities[0], entities[1], entities[2]], detection.ReadDistances().ToArray().Select(value => value.First));
        Assert.Equal([entities[1], entities[2], entities[3]], detection.ReadDistances().ToArray().Select(value => value.Second));
        simulation.Schedule(1, .01, .01).Complete();

        ContactView view = contactResults.Contacts.OpenView();
        var current = ContactFeatureKeys(view);
        Assert.True(original.SetEquals(current));
        foreach (ContactResponse responseValue in view.Responses) Assert.True(responseValue.ForceOnSecond.Length() > 0);
        Assert.InRange(world.ReadComponentData<BodyPose>(entities[3]).Position.X, 4.751f, 4.8f);
        Assert.Equal(1, detection.Number);
        Assert.Equal(.02, detection.Time);
    }

    private static HashSet<(Entity First, Entity Second, uint FirstShape, uint SecondShape, int FirstFeature, int SecondFeature, int Point)> ContactFeatureKeys(ContactView view)
    {
        var values = new HashSet<(Entity, Entity, uint, uint, int, int, int)>();
        for (int row = 0; row < view.PointCount; row++)
        {
            ContactPoint point = view.GetPoint(row); ContactPair pair = view.Pairs[point.PairIndex];
            values.Add((pair.FirstGeometry, pair.SecondGeometry, pair.FirstShape, pair.SecondShape, point.FirstFeature, point.SecondFeature, point.PointIndex));
        }
        return values;
    }
    private ComputePipelineDesc CompileMixedGeometry(string type)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("mixed_geometry", $$"""
            #include "physics_geometry_update.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Update(uint3 id:SV_DispatchThreadID) { updateDeviceGeometry<{{type}}>(id.x); }
            """, [new("Update", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
    private ComputePipelineDesc CompileMixedDetection(string type)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("mixed_contact_detection", $$"""
            #include "physics_geometry_references.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id : SV_DispatchThreadID)
            { {{type}} kernel; detectDeviceReferencedCollision(id.x,kernel); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
}
