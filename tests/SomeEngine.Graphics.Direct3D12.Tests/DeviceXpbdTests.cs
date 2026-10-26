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

[Collection("Direct3D12 shader compilation")]
[Trait("Category", "Hardware")]
public sealed partial class DeviceXpbdTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void AReplacementConstraintUsesItsOwnUploadedParametersForTheSelectedDeviceRow()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 1, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceLengthMultipliers");
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity second = AddBody(world, bodies, 1, new(2, 0, 0), BodyMotion.Dynamic);
        Entity prior = world.CreateRelation(first, second, new XpbdLength { RestLength = 1, Compliance = .01f }).Entity;
        world.Add(prior, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(prior, new DeviceXpbdState<float>(multipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        using var write = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var project = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, rows,
            bodies, dynamics, lengths, multipliers, CompileLength(), 64);
        var upload = lengths.BeginInputs().Then(write).Then(lengths.UploadInputs());
        PhysicsSimulation.Create(world, QueryDefinition.Empty, upload, 1).Schedule(0, 0, .1).Complete();
        world.DestroyEntity(prior);
        Entity replacement = world.CreateRelation(first, second, new XpbdLength { RestLength = 3, Compliance = .01f }).Entity;
        world.Add(replacement, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 0));
        world.Add(replacement, new DeviceXpbdState<float>(multipliers));
        var solve = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(2)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, solve, 1).Schedule(1, .1, .1).Complete();

        Near(new(2, 0, 0), world.Read<BodyPose>(second).Position);
        Near(Vector3.Zero, world.Read<BodyVelocity>(second).Linear);

        PhysicsSimulation.Create(world, QueryDefinition.Empty, upload.Then(solve), 1).Schedule(2, .2, .1).Complete();

        Near(new(2.5f, 0, 0), world.Read<BodyPose>(second).Position);
        Near(new(5, 0, 0), world.Read<BodyVelocity>(second).Linear);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
    }

    [Fact]
    public void AReplacementBodyKeepsItsOwnStateWhenThePriorDeviceResultIsConsumed()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(first, new BodyVelocity { Linear = Vector3.UnitX });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var compute = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(dynamics.UpdateVelocities());
        QueryDefinition selected = world.QueryDefinition().All<DeviceBodyBinding>().Build();
        PhysicsStepResult prior = PhysicsSimulation.Create(world, selected, compute, 1).Schedule(7, 0, .2);
        prior.Complete();
        world.DestroyEntity(first);
        Entity replacement = AddBody(world, bodies, 0, new(100, 0, 0), BodyMotion.Dynamic);
        world.Replace(replacement, new BodyVelocity { Linear = new(7, 0, 0) });
        world.Replace(replacement, new BodyStepState { Number = 29, Time = 4 });

        PhysicsSimulation.Create(world, selected, results.Readback(64), 1).Schedule(7, 0, .2, prior.Completion).Complete();

        Assert.Equal(first.Index, replacement.Index);
        Assert.NotEqual(first.Generation, replacement.Generation);
        Near(new(100, 0, 0), world.Read<BodyPose>(replacement).Position);
        Near(new(7, 0, 0), world.Read<BodyVelocity>(replacement).Linear);
        Assert.Equal(29, world.Read<BodyStepState>(replacement).Number);
        Assert.Equal(4, world.Read<BodyStepState>(replacement).Time);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceBendReadsProtectedParticipationEdgesAndCorrectsTheThreeOriginalBodies()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var bends = new DeviceConstraintData<DeviceBendConstraint>(backend, device, 2, "DeviceBends");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 2, "DeviceBendMultipliers");
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity middle = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity last = AddBody(world, bodies, 2, new(1, 1, 0), BodyMotion.Dynamic);
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f }, [first, middle, last]);
        world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(bends, 1));
        world.Add(bend, new DeviceXpbdState<float>(multipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle constraints = world.Query(world.QueryDefinition().Read<XpbdBend>().ReadBuffer<ConstraintBodyLink>()
            .Optional<DirectedRelationEndpoints<ConstraintParticipant>>(QueryAccess.Read)
            .Read<DeviceConstraintBinding<DeviceBendConstraint>>().All<DeviceXpbdState<float>>());
        using var write = bends.WriteInputs<XpbdBend, DeviceBendSource>(world, constraints, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var project = new DeviceXpbdProjection<DeviceBendConstraint, float>(backend, device, world, constraints,
            bodies, dynamics, bends, multipliers, CompileBend(), 64);
        var stages = bends.BeginInputs().Then(write).Then(bends.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(1)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1).Schedule(0, 0, .1).Complete();

        Near(new(0, -.125f, 0), world.Read<BodyPose>(first).Position);
        Near(new(.875f, .125f, 0), world.Read<BodyPose>(middle).Position);
        Near(new(1.125f, 1, 0), world.Read<BodyPose>(last).Position);
        Near(new(1.25f, 0, 0), world.Read<BodyVelocity>(last).Linear);
        world.ReleaseQuery(constraints);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void ProjectConstraintReadersAndKernelsUseTheSameCompletedDeviceStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 4, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 4, "DeviceLengthMultipliers");
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity second = AddBody(world, bodies, 1, new(2, 0, 0), BodyMotion.Dynamic);
        RelationEdge<XpbdLength> edge = world.CreateRelation(first, second, new XpbdLength { RestLength = 1, Compliance = .01f });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 3));
        world.Add(edge.Entity, new DeviceXpbdState<float>(multipliers));
        world.Add(edge.Entity, new ProjectLengthScale(1.5f));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle constraints = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<ProjectLengthScale>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>().Read<DeviceConstraintBinding<DeviceLengthConstraint>>()
            .All<DeviceXpbdState<float>>());
        using var write = lengths.WriteInputs<XpbdLength, ProjectLengthSource>(world, constraints, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using SomeEngine.Graphics.Buffer scale = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(scale, MapType.Write, BufferRange.Whole))
        {
            float value = .5f;
            MemoryMarshal.Write(mapping.Bytes, in value);
            mapping.Flush(mapping.Range);
        }
        using BufferSrv scaleView = backend.CreateBufferSrv(device, new(scale, new(0, 4), StructureStride: 4));
        using var project = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, constraints,
            bodies, dynamics, lengths, multipliers, CompileProjectLength(), 64,
            new Dictionary<string, ResourceBinding[]> { ["ProjectRestScales"] = [ResourceBinding.ReadOnlyBuffer(scaleView)] },
            new[] { JobResourceAccess.Read(jobs.GetResourceToken(scale), 0, 4) });
        var stages = lengths.BeginInputs().Then(write).Then(lengths.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(2)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1).Schedule(0, 0, .1).Complete();

        Near(new(1.375f, 0, 0), world.Read<BodyPose>(second).Position);
        Near(new(-6.25f, 0, 0), world.Read<BodyVelocity>(second).Linear);
        world.ReleaseQuery(constraints);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceGyroscopicTorqueMatchesTheFullInertiaBodyMotion()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 1);
        Entity body = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity expected = reference.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        var mass = new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
            CenterOfMass = new(.25f, -.1f, .2f),
            InverseInertia = new(1, .2f, 0, 0, .2f, .5f, .1f, 0, 0, .1f, .25f, 0, 0, 0, 0, 1) };
        var velocity = new BodyVelocity { Angular = new(1, 2, 3) };
        world.Replace(body, mass); world.Replace(body, velocity);
        reference.Add(expected, mass); reference.Add(expected, velocity); reference.Add(expected, new BodyStepState());
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyGyroscope(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var gpu = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(dynamics.ApplyGyroscopicTorque())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var cpu = new ApplyGyroscopicTorque(64).Then(new PredictBodies(Vector3.Zero, 64)).Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, cpu, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), gpu, 1).Schedule(0, 0, .1).Complete();

        Near(reference.Read<BodyPose>(expected).Position, world.Read<BodyPose>(body).Position);
        Near(reference.Read<BodyVelocity>(expected).Angular, world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(MathF.Abs(Quaternion.Dot(reference.Read<BodyPose>(expected).Rotation,
            world.Read<BodyPose>(body).Rotation)), .999999f, 1.000001f);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DeviceLengthUsesTheOriginalRelationComplianceAndAccumulatesItsSubstepMultiplier()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 4, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 4, "DeviceLengthMultipliers");
        Entity first = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity second = AddBody(world, bodies, 1, new(2, 0, 0), BodyMotion.Dynamic);
        RelationEdge<XpbdLength> edge = world.CreateRelation(first, second, new XpbdLength { RestLength = 1, Compliance = .01f });
        world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, 3));
        world.Add(edge.Entity, new DeviceXpbdState<float>(multipliers));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle constraints = world.Query(world.QueryDefinition().Read<XpbdLength>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>().Read<DeviceConstraintBinding<DeviceLengthConstraint>>()
            .All<DeviceXpbdState<float>>());
        using var write = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, constraints, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var project = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, constraints, bodies, dynamics, lengths, multipliers,
            CompileLength(), threadsPerGroup: 64);
        var stages = lengths.BeginInputs().Then(write).Then(lengths.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(project.Initialize()).Then(project.Iterate(2)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 1)
            .Schedule(3, 1, .1).Complete();

        Near(Vector3.Zero, world.Read<BodyPose>(first).Position);
        Near(new(1.5f, 0, 0), world.Read<BodyPose>(second).Position);
        Near(new(-5, 0, 0), world.Read<BodyVelocity>(second).Linear);
        world.ReleaseQuery(constraints);
        world.ReleaseQuery(members);
    }

    [Fact]
    public void DevicePredictionAndVelocityReconstructionReturnTheCompletedBodyState()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 4);
        Entity body = world.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
            InverseInertia = Matrix4x4.Identity, CenterOfMass = new(.25f, 0, 0) });
        world.Add(body, new BodyVelocity { Linear = Vector3.UnitX, Angular = new(0, 0, MathF.PI) });
        world.Add(body, new BodyStepState());
        world.Add(body, new DeviceBodyBinding(bodies, 3));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies,
            CompileBodyDynamics(), threadsPerGroup: 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64));
        var physics = PhysicsSimulation.Create(world, world.QueryDefinition().All<DeviceBodyBinding>().Build(), stages, 2);

        PhysicsStepResult completed = physics.Schedule(17, 2, .2);
        completed.Complete();

        Near(new(.24774575f, -.44694632f, 0), world.Read<BodyPose>(body).Position);
        Near(new(1, -2, 0), world.Read<BodyVelocity>(body).Linear);
        Near(new(0, 0, MathF.PI), world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(Quaternion.Dot(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * .2f),
            world.Read<BodyPose>(body).Rotation), .999999f, 1.000001f);
        Assert.Equal(17, world.Read<BodyStepState>(body).Number);
        Assert.Equal(1, world.Read<BodyStepState>(body).Substep);
        Assert.Equal(2.2, world.Read<BodyStepState>(body).Time);
        world.ReleaseQuery(members);
    }

    private DeviceBodyKernels CompileBodyDynamics()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("body_dynamics", "#include \"physics_body_dynamics.slang\"",
            [new("PredictDeviceBodies", SlangStage.Compute), new("UpdateDeviceBodyVelocities", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private DeviceXpbdKernels CompileLength()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_length", """
            #include "physics_xpbd_length.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdLength>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdLength>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private DeviceXpbdKernels CompileBend()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_bend", """
            #include "physics_xpbd_bend.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdBend>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdBend>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private DeviceBodyKernels CompileBodyGyroscope()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("body_gyroscope", "#include \"physics_body_dynamics.slang\"",
            [new("PredictDeviceBodies", SlangStage.Compute), new("UpdateDeviceBodyVelocities", SlangStage.Compute),
                new("ApplyDeviceGyroscopicTorque", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2));
    }

    private DeviceXpbdKernels CompileProjectLength()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("project_xpbd", """
            #include "physics_xpbd_length.slang"
            #include "physics_xpbd.slang"
            StructuredBuffer<float> ProjectRestScales;
            struct ProjectLength : IDeviceXpbdProjection
            {
                bool active(uint row) { return DeviceLengthsMetadata[row].active != 0; }
                void initialize(uint row, DeviceXpbdStep context) { DeviceLengthMultipliers[row] = 0; }
                void project(uint row, DeviceXpbdStep context)
                {
                    DeviceLengthConstraint data = DeviceLengths[row];
                    DeviceBodyState first = DeviceBodyStates[data.firstBody], second = DeviceBodyStates[data.secondBody];
                    float3 delta = second.position - first.position;
                    float distance = length(delta);
                    float firstWeight = first.motion == 2 ? first.inverseMass : 0;
                    float secondWeight = second.motion == 2 ? second.inverseMass : 0;
                    float alpha = data.compliance / (context.duration * context.duration);
                    float multiplier = DeviceLengthMultipliers[row];
                    float change = (-(distance - data.restLength * ProjectRestScales[0]) - alpha * multiplier) /
                        (firstWeight + secondWeight + alpha);
                    DeviceLengthMultipliers[row] = multiplier + change;
                    if (firstWeight > 0) first.position -= firstWeight * delta / distance * change;
                    if (secondWeight > 0) second.position += secondWeight * delta / distance * change;
                    DeviceBodyStates[data.firstBody] = first; DeviceBodyStates[data.secondBody] = second;
                }
            };
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<ProjectLength>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<ProjectLength>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }

    private readonly record struct ProjectLengthScale(float Value) : IComponent;
    private readonly struct ProjectLengthSource : IDeviceConstraintSource<XpbdLength, DeviceLengthConstraint>
    {
        public static void Collect(in XpbdLength component, ref JobResourceAccessBuilder resources) { }
        public DeviceLengthConstraint Read(in XpbdLength component, scoped QueryRow row, scoped QueryCursor access)
        {
            DeviceLengthConstraint data = new DeviceLengthSource().Read(in component, row, access);
            data.RestLength *= row.Read<ProjectLengthScale>().Value;
            return data;
        }
    }

    private static Entity AddBody(World world, DeviceBodies bodies, int index, Vector3 position, BodyMotion motion)
    {
        Entity entity = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(entity, new BodyMass { Motion = motion, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(entity, new BodyVelocity());
        world.Add(entity, new BodyStepState());
        world.Add(entity, new DeviceBodyBinding(bodies, index));
        return entity;
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders")))
                return Path.Combine(directory.FullName, "assets", "Shaders");
        throw new DirectoryNotFoundException();
    }

    private static Device CreateDevice(IGraphicsBackend backend)
    {
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        return backend.CreateDevice(new(adapters.First(value => value.HardwareAccelerated).Id,
            [new(QueueType.Compute)], label: "Device XPBD behavior"));
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 1e-5f);
}
