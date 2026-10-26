using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
