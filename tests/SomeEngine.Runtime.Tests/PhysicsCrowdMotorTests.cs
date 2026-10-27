using System.Numerics;
using System.Runtime.InteropServices;
using DefaultGame;
using SlangShaderSharp;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;
using SomeEngine.Render;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
public sealed class PhysicsCrowdMotorTests
{
    [Fact]
    public void WorldAndSourceMotorsConsumeTheCurrentAnimationAndComponentEdits()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.IndirectCommands));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var example = PhysicsCrowdExample.CreateDevice(2, backend, device);
        using var scene = example.CreateSimulation(world);
        var drivers = new[] { scene.WorldDrivers[0], scene.ParentDrivers[0] };
        var storage = new PoseStorage();
        using SkeletonInstance sampled = storage.CreateInstance(example.Character.Skeleton);
        AnimationClip clip = example.Character.Clips[1].CreateClip(example.Character.Skeleton);
        var local = new BoneTransform[CrowdExample.BoneCount];
        var previous = new Vector3[2];
        for (int actor = 0; actor < 2; actor++) previous[actor] = Position(actor, actor);
        using var targets = new ReadMotorTargets(backend, device,
            world.Read<DeviceConstraintBinding<DeviceMotorConstraint>>(drivers[0]).Data,
            world.Read<DeviceConstraintBinding<DeviceJointConstraint>>(drivers[0]).Data);

        for (int number = 0; number < 3; number++)
        {
            if (number == 1)
                foreach (var driver in drivers)
                {
                    JointMotor motor = world.Read<JointMotor>(driver); motor.LinearStiffness = new(75); world.Replace(driver, motor);
                    JointMotorTarget target = world.Read<JointMotorTarget>(driver);
                    target.Frame = target.Frame with { Position = target.Frame.Position + new Vector3(.1f, 0, 0) };
                    world.Replace(driver, target);
                }
            scene.AdvanceTime(scene.StepSeconds);
            PhysicsStepResult completed = scene.CompleteForRead();
            Vector4[] observed = targets.Read();
            for (int actor = 0; actor < 2; actor++)
            {
                Vector3 expected = Position(actor, actor + (float)completed.EndTime);
                if (number > 0) expected.X += .1f;
                Assert.InRange(Vector3.Distance(expected, new(observed[actor * 2].X, observed[actor * 2].Y, observed[actor * 2].Z)), 0, 2e-5f);
                Assert.InRange(Math.Abs(observed[actor * 2].W - completed.EndTime), 0, 1e-7);
                Vector3 velocity = (expected - previous[actor]) / (float)scene.StepSeconds;
                Assert.InRange(Vector3.Distance(velocity, new(observed[actor * 2 + 1].X, observed[actor * 2 + 1].Y, observed[actor * 2 + 1].Z)), 0, .002f);
                Assert.Equal(number == 0 ? 50 : 75, observed[actor * 2 + 1].W);
                previous[actor] = expected;
            }
        }

        Vector3 Position(int actor, float time)
        {
            clip.SampleLocal(time % clip.Duration, local); sampled.Update(local, time);
            GpuPoseTarget target = world.Read<GpuPoseTarget>(scene.CharacterTargets[actor]);
            Vector3 hand = Vector3.Transform(sampled.ModelPose.Transforms[22].Translation, target.ModelToWorld);
            return actor == 0 ? hand : hand - Vector3.Transform(sampled.ModelPose.Transforms[20].Translation, target.ModelToWorld);
        }
    }

    private sealed class ReadMotorTargets : IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly Device _device;
        private readonly Buffer _output, _readback;
        private readonly BufferUav _view;
        private readonly Pipeline _pipeline;
        private readonly VariableLayoutReflection _layout;
        private readonly ResourceBinding[] _bindings;
        private readonly CommandContext _commands;
        private bool _initialized;

        internal ReadMotorTargets(IGraphicsBackend backend, Device device, DeviceConstraintData<DeviceMotorConstraint> motors,
            DeviceConstraintData<DeviceJointConstraint> joints)
        {
            _backend = backend; _device = device;
            IGlobalSession global = Shader.CreateSlangGlobalSession();
            TargetDesc target = new() { Format = device.Capabilities.CompileTarget, Profile = global.FindProfile("sm_6_0") };
            global.CreateSession(new SessionDesc { Targets = [target], SearchPaths = [Path.Combine(AppContext.BaseDirectory, "PhysicsShaders")] }, out ISession session);
            IModule unit = session.LoadModuleFromSource("read_motor_targets", "read_motor_targets.slang", Slang.CreateBlob("""
                #include "physics_motor_data.slang"
                RWStructuredBuffer<float4> ObservedTargets;
                [shader("compute")][numthreads(2,1,1)]
                void Read(uint3 id:SV_DispatchThreadID)
                {
                    DeviceMotorConstraint motor=DeviceMotors[id.x];
                    ObservedTargets[id.x*2]=float4(motor.targetPosition,float(asdouble(motor.targetTime.x,motor.targetTime.y)));
                    ObservedTargets[id.x*2+1]=float4(motor.targetLinearVelocity,motor.linearStiffness.x);
                }
                """), out ISlangBlob? diagnostics) ?? throw new InvalidDataException(diagnostics?.AsString);
            unit.FindEntryPointByName("Read", out IEntryPoint entry);
            session.CreateCompositeComponentType([unit, entry], out IComponentType combined, out diagnostics);
            Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, diagnostics?.AsString);
            Shader shader = Shader.CreateFromSlang("Read motor targets", global, session, [unit], [entry], linked, [target], generateLibraryCode: false);
            EntryPointReflection reflection = shader.RequireSlangEntry(0, "Read", SlangStage.Compute);
            var kernel = new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0), reflection,
                shader.GetSlangEntryBytes(0, reflection), "Read", CodeBlob: shader.GetSlangEntryBlob(0, reflection));
            _pipeline = backend.CreateComputePipeline(device, kernel);
            _layout = kernel.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            _output = backend.CreateBuffer(device, new(64, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
            _readback = backend.CreateBuffer(device, new(64, BufferUsages.CopyDestination), MemoryType.Readback);
            _view = backend.CreateBufferUav(device, new(_output, new(0, 64), StructureStride: 16));
            var bindings = new Dictionary<string, ResourceBinding[]>(); motors.AddBindings(bindings); joints.AddBindings(bindings);
            bindings.Add("ObservedTargets", [ResourceBinding.WritableBuffer(_view)]);
            _bindings = ShaderResourceLayout.Resolve<ResourceBinding>(_layout, (name, _) => bindings[name][0]);
            _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        }

        internal Vector4[] Read()
        {
            _backend.Begin(_commands, new(0, 0, 8));
            if (!_initialized)
                _backend.Barrier(_commands, new BufferBarrier(_output, _output.InitialSync, PipelineSync.ComputeShading,
                    _output.InitialAccess, ResourceAccess.UnorderedAccess));
            _backend.SetPipeline(_commands, _pipeline);
            _backend.SetTransientParameterBindings(_commands, new(_layout, _bindings, []));
            _backend.Dispatch(_commands, new(1, 1, 1));
            _backend.Barrier(_commands, new BufferBarrier(_output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            _backend.CopyBuffer(_commands, new(_output, 0, _readback, 0, 64));
            _backend.Barrier(_commands, new BufferBarrier(_output, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            using RecordedCommands recorded = _backend.End(_commands);
            QueueCompletion completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
            _initialized = true;
            Assert.Equal(WaitStatus.Completed, _backend.WaitCpu(in completion, TimeSpan.FromSeconds(60)));
            using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole); mapping.Invalidate(mapping.Range);
            return MemoryMarshal.Cast<byte, Vector4>(mapping.Bytes).ToArray();
        }

        public void Dispose() { _commands.Dispose(); _view.Dispose(); _output.Dispose(); _readback.Dispose(); _pipeline.Dispose(); }
    }
}
