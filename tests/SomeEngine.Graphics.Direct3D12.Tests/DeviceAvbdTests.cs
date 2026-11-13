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

[Collection("Direct3D12 shader compilation")]
[Trait("Category", "Hardware")]
public sealed partial class DeviceAvbdTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(100, 900, 1.5f)]
    [InlineData(300, 900, 1.25f)]
    [InlineData(100, 100, 1.5f)]
    public void DeviceElasticityKeepsMaterialStiffnessIndependentOfItsPenalty(float stiffness, float penalty, float position)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
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
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>()
            .WithStored<RelationEndpoints<ElasticConnection>>());
        Query stateRows = world.Query(new QueryDescription().WithStored<AvbdElasticState>()
            .WithStored<DeviceConstraintBinding<DeviceElasticConstraint>>().WithStored<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var inputs = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint,
            DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(backend, device, world, stateRows, parameters, histories, default);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
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
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileAvbd(),
            new AvbdIterationSettings(32, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), threadsPerGroup: 64, selections: new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(inputs).Then(parameters.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(2, 1, .1).Complete();

        Assert.InRange(Vector3.Distance(new(position, 0, 0), world.ReadComponentData<BodyPose>(moving).Position), 0, 1e-5f);
        Assert.InRange(Vector3.Distance(new((position - 2) / .1f, 0, 0), world.ReadComponentData<BodyVelocity>(moving).Linear), 0, 1e-4f);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(anchor).Position);
        Assert.Equal(1.1, world.ReadComponentData<BodyStepState>(moving).Time);
    }

    [Fact]
    public void DeviceContactCombinesRepeatedBodyAnchorsBeforeItsSixDimensionalSolve()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Set(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        using var source = CreateAvbdContactValues(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }, new() { NormalStiffness = 100 }, [new(floor, Vector3.Zero, 1), new(moving, -Vector3.UnitX, .5f), new(moving, Vector3.UnitX, .5f)], new() { NormalPenalty = 900 });
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 3), default);
        using var input = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 3), bindings, reads, 64, contacts: contacts);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 0, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(input.Upload())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(3, 1, .1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.Y, .947367f, .947370f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Angular.Length(), 0, 1e-5f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Linear.Y, -.52633f, -.52630f);
        Assert.InRange(contactResults.Contacts.OpenView().Responses[0].ForceOnSecond.Y, 4.7367f, 4.7370f);
        Assert.Equal(1.1, contactResults.Contacts.OpenView().Responses[0].EndTime);
    }

    [Fact]
    public void DeviceOffCenterElasticityCouplesTranslationRotationAndTheFullInertiaTensor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, new(2, 1, 0), BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Set(moving, new BodyMass
        {
            Motion = BodyMotion.Dynamic,
            InverseMass = 1,
            CenterOfMass = Vector3.UnitX,
            InverseInertia = new(1, 0, .5f, 0, 0, 1, 0, 0, .5f, 0, 1, 0, 0, 0, 0, 1),
        });
        using var parameters = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var histories = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        Entity relation = world.CreateRelation(anchor, moving, new ElasticConnection
        { SecondAnchor = Vector3.UnitY, RestRotation = Quaternion.Identity, LinearStiffness = 100 }).Entity;
        world.Add(relation, new AvbdElasticState { LinearPenalty = 900 });
        world.Add(relation, new DeviceConstraintBinding<DeviceElasticConstraint>(parameters, 0));
        world.Add(relation, new DeviceAvbdState<DeviceAvbdElasticState>(histories));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        Query connections = world.Query(new QueryDescription().WithStored<ElasticConnection>().WithStored<RelationEndpoints<ElasticConnection>>());
        Query stateRows = world.Query(new QueryDescription().WithStored<AvbdElasticState>()
            .WithStored<DeviceConstraintBinding<DeviceElasticConstraint>>().WithStored<DeviceAvbdState<DeviceAvbdElasticState>>());
        using var inputs = parameters.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint,
            DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(backend, device, world, stateRows, parameters, histories, default);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        parameters.AddBindings(bindings); histories.AddBindings(bindings);
        using var selected = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(backend, device,
            world, connections, parameters, "DeviceElasticConstraintsSelection");
        using var selectedStates = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, connections, parameters, new(histories), "DeviceElasticAvbdSelection");
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileGraph(),
            new(1, 2), bindings, parameters.GetReadAccesses(jobs), 64, selections: new[] { selected });
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileAvbd(), new(1, 1, 0, 0), bindings,
            parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), 64, selections: new[] { selectedStates });
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(inputs).Then(parameters.UploadInputs()).Then(stateInputs.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(0, 0, .1).Complete();

        BodyPose pose = world.ReadComponentData<BodyPose>(moving);
        BodyMass mass = world.ReadComponentData<BodyMass>(moving);
        // One point spring and inertial energy have this closed-form six-dimensional Schur response.
        Assert.InRange(Vector3.Distance(new(1.7243428f, -.2230256f, .0609710f), pose.GetCenterOfMass(in mass)), 0, 1e-5f);
        Assert.InRange(MathF.Abs(Quaternion.Dot(new(-.0918891f, .0295358f, -.2280818f, .9688461f), pose.Rotation)), .99999f, 1.00001f);
        Assert.InRange(Vector3.Distance(new(7.243428f, -2.230256f, .609710f), world.ReadComponentData<BodyVelocity>(moving).Linear), 0, 1e-4f);
    }

    [Fact]
    public void DeviceContactRestitutionUsesTheCommonForceAndWritesTheSelectedBodies()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Remove<DeviceAvbdEquationBinding>(floor);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Set(moving, new BodyVelocity { Linear = new(0, -2, 0) });
        using var source = CreateAvbdContactValues(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }, new() { NormalStiffness = 1e6f, Restitution = .5f, RestitutionThreshold = .1f }, [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)], new() { NormalPenalty = 1e6f });
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), default);
        using var input = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, contacts: contacts);
        using var solve = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(32, 1, 0, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(input.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(response.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(response.ApplyRestitution(1)).Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(7, 2, .1).Complete();

        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Linear.Y, .9999f, 1.0001f);
        Assert.InRange(contactResults.Contacts.OpenView().Responses[0].RestitutionImpulse, 1, 1.001f);
        Assert.Equal(2.1, contactResults.Contacts.OpenView().Responses[0].EndTime);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyVelocity>(floor).Linear);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(floor).Position);
    }

    private static ContactManifolds<DeviceAvbdContactState> CreateAvbdContactValues(Scheduler jobs, Entity first, Entity second,
        ContactPoint point, ContactMaterial material, ReadOnlySpan<ContactBodyLink> links, DeviceAvbdContactState state)
    {
        var values = new ContactManifolds<DeviceAvbdContactState>(jobs, state);
        values.Resize(1, 1, links.Length); values.Columns.Pairs[0] = new(first, second, 0, 0, 0, 1);
        values.Columns.SetPoint(0, in point); values.Columns.Materials[0] = material;
        values.Columns.BodyStarts[0] = 0; values.Columns.BodyCounts[0] = links.Length;
        links.CopyTo(values.Columns.BodyLinks); values.States[0] = state;
        return values;
    }

    private static Entity CreateBody(World world, DeviceBodies bodies, DeviceAvbdBodyEquations equations,
        int index, Vector3 position, BodyMotion motion)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
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
    private AvbdGraphKernels CompileAvbd()
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
    private DeviceGraphKernels CompileContactGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_contact_graph", """
            #include "physics_contact_data.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceContactBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private AvbdGraphKernels CompileContactAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_contact_step", """
            #include "physics_avbd_contact.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceContactAvbdConstraints rows; initializeDeviceAvbd(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID) { DeviceContactAvbdConstraints rows; updateDeviceAvbdBody(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID) { DeviceContactAvbdConstraints rows; updateDeviceAvbdDuals(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private DeviceContactKernels CompileContactActions()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("device_contact_responses", """
            #include "physics_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> rows; initializeDeviceContactResponses(id.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> rows; applyDeviceContactRestitution(id.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("Restore", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
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
