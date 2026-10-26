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

[Collection("Direct3D12 shader compilation")]
[Trait("Category", "Hardware")]
public sealed class DeviceAvbdTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(100, 900, 1.5f)]
    [InlineData(300, 900, 1.25f)]
    [InlineData(100, 100, 1.5f)]
    public void DeviceElasticityKeepsMaterialStiffnessIndependentOfItsPenalty(float stiffness, float penalty, float position)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, new(2, 0, 0), BodyMotion.Dynamic);
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        var relation = world.CreateRelation(anchor, moving, new ElasticConnection
        { FirstAnchor = Vector3.UnitX, RestRotation = Quaternion.Identity, LinearStiffness = stiffness });
        world.Add(relation.Entity, new AvbdElasticState { LinearPenalty = penalty });
        world.Add(relation.Entity, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(relation.Entity, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>()
            .Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var inputs = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint,
            DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(backend, device, world, stateRows, parameters, histories, default);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(backend, device,
            world, connections, parameters, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, connections, parameters, new(histories), "DeviceElasticAvbdSelection");
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), histories.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileGraph(),
            new DeviceGraphCapacity(1, 2), bindings, reads, threadsPerGroup: 64, selections: new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileAvbd(),
            new DeviceAvbdSettings(32, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), threadsPerGroup: 64, selections: new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(parameters.BeginInputs()).Then(inputs).Then(parameters.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(2, 1, .1).Complete();

        Assert.InRange(Vector3.Distance(new(position, 0, 0), world.Read<BodyPose>(moving).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new((position - 2) / .1f, 0, 0), world.Read<BodyVelocity>(moving).Linear), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(anchor).Position);
        Assert.Equal(1.1, world.Read<BodyStepState>(moving).Time);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(connections); world.ReleaseQuery(members);
    }

    private static Entity CreateBody(World world, DeviceBodies bodies, DeviceAvbdBodyEquations equations,
        int index, Vector3 position, BodyMotion motion)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = motion, InverseMass = motion == BodyMotion.Dynamic ? 1 : 0 });
        world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState()); world.Add(body, new AvbdBodyEquation());
        world.Add(body, new DeviceBodyBinding(bodies, index)); world.Add(body, new DeviceAvbdEquationBinding(equations, index));
        return body;
    }
    private DeviceBodyKernels CompileDynamics()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_body_step", "#include \"physics_body_dynamics.slang\"",
            [new("PredictDeviceBodies", SlangStage.Compute), new("UpdateDeviceBodyVelocities", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceGraphKernels CompileGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_elastic_graph", """
            #include "physics_elastic_data.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceElasticBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_elastic_step", """
            #include "physics_avbd_elastic.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceElasticAvbdConstraints rows; initializeDeviceAvbd(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID) { DeviceElasticAvbdConstraints rows; updateDeviceAvbdBody(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID) { DeviceElasticAvbdConstraints rows; updateDeviceAvbdDuals(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private static ComputePipelineDesc Entry(D3D12TestShader shader, int index) =>
        new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders"))) return Path.Combine(directory.FullName, "assets", "Shaders");
        throw new DirectoryNotFoundException();
    }
    private static Device CreateDevice(IGraphicsBackend backend)
    {
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count]; backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        return backend.CreateDevice(new(adapters.First(value => value.HardwareAccelerated).Id, [new(QueueType.Compute)], label: "Device AVBD behavior"));
    }
}
