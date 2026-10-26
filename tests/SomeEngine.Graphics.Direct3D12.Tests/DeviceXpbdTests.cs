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
public sealed class DeviceXpbdTests(D3D12TestCompiler shaderCompiler)
{
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
        using var project = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, constraints, bodies, lengths, multipliers,
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
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdLength>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id : SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdLength>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection,
            shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
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
