using System.Numerics;
using SomeEngine.Animation.Pose;
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
    public void HeldGameSourceFeedsTheCommonBodyStepAndMapsItsCompletedBonesBeforeTheAimStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        BoneTransform Bone(Vector3 value) => new() { Translation = value, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0, 1], [Bone(new(0, 2, 0)), Bone(-Vector3.UnitY), Bone(new(.2f, 0, 0))], [1, 2, 3]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        var chain = new GpuXpbdChain(skeleton, [new(0), new(1)], new(0, -10, 0),
            lengthCompliance: .01f, damping: 0, step: .1f, iterations: 1);
        var aim = new GpuAimChain(skeleton, [new(1, Vector3.UnitX, 1, MathF.PI / 2)]);
        Entity target = GpuPoseAnimation.CreateTarget(world, instance);
        GpuPoseTarget input = world.ReadComponentData<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuAimPose(new GpuXpbdPose(blend, chain), aim, 0));
        input.Playing = true; input.InputNumber = 1; input.CalculationTime = 1.1; input.PlaybackTime = .1f; input.SampleTime = .1f;
        world.Set(target, input);
        world.AddBuffer<GpuPoseGoal>(target);
        { using var goals = world.GetBuffer<GpuPoseGoal>(target); goals.Add(new() { Position = new(2, 1, 0), PositionWeight = 1 }); }
        Query targets = world.Query(new QueryDescription().WithStored<GpuPoseTarget>());
        using var poseBodies = new DevicePosePhysics(backend, device, world, new(3, 1, 1));
        poseBodies.Bind(target);
        Entity[] actor = poseBodies.GetBodies(target, 0).ToArray();
        using var equations = new DeviceAvbdBodyEquations(backend, device, poseBodies.Bodies);
        world.Add(actor[0], new DeviceAvbdEquationBinding(equations, 0));
        world.Add(actor[1], new DeviceAvbdEquationBinding(equations, 1));
        Entity floor = CreateBody(world, poseBodies.Bodies, equations, 2, Vector3.Zero, BodyMotion.Static);
        using var sources = new DevicePoseSources(backend, device, world, targets,
            DevicePoseShaders.Get(device.Capabilities.CompileTarget), new(1, 3, 1, 3, 1, 0, 1, 2), poseBodies);
        var poseStages = sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithStored<DeviceAvbdEquationBinding>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<DeviceConstraintBinding<DeviceLengthConstraint>>().WithStored<DeviceXpbdState<float>>());
        using var bodyInputs = new DeviceBodyInputs(world, members, poseBodies.Bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, poseBodies.Bodies, CompileDynamics(), 64);
        using var cpuContacts = new ContactManifolds<DeviceAvbdContactState>(jobs, new() { NormalPenalty = 900 });
        jobs.Schedule(new SeedPoseBodyContact(cpuContacts, floor, actor[1]), [cpuContacts.WriteAccess]).Complete();
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), new() { NormalPenalty = 900 });
        using var writeContacts = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, cpuContacts, contacts);
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengths, poseBodies.Lengths, new(poseBodies.LengthMultipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.Current.AddBindings(bindings);
        poseBodies.Lengths.AddBindings(bindings); poseBodies.LengthMultipliers.AddBindings(bindings);
        JobResourceAccess[] reads = poseBodies.Lengths.GetReadAccesses(jobs);
        using var graph = new DeviceConstraintGraph(backend, device, poseBodies.Bodies, dynamics, CompileComplementaryGraph(), new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedLengths }, contacts: contacts);
        JobResourceAccess[] solveAccesses = [.. reads, poseBodies.LengthMultipliers.GetReadAccess(jobs)];
        using var avbd = new AvbdGraphCommands(backend, device, poseBodies.Bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 0, 0),
            bindings, solveAccesses, 64);
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengths,
            poseBodies.Bodies, dynamics, poseBodies.Lengths, poseBodies.LengthMultipliers, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, poseBodies.Bodies, graph, contacts, CompileContactActions(), bindings,
            reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var results = new DeviceBodyResults(backend, device, poseBodies.Bodies);
        var inputContext = new PhysicsStepContext(world, members, 7, 0, 1, 1, .1);
        writeContacts.Upload().Schedule(in inputContext, default).Complete();
        var stages = poseBodies.Upload().Then(bodyInputs.Upload()).Then(poseStages.WriteTargets())
            .Then(dynamics.Predict(chain.Acceleration)).Then(response.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(poseStages.MapBones()).Then(results.Readback(64)).Then(sources.Readback()).Then(sources.CopyOutput());
        var simulation = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1);

        simulation.Schedule(7, 1, .1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(actor[1]).Position.Y, .96665f, .96668f);
        Assert.InRange(sources.ReadModels(target)[1].Translation.Y, .96665f, .96668f);
        Assert.InRange(Vector3.Distance(new(.19997223f, .9699996f, 0), sources.ReadModels(target)[2].Translation), 0, .001f);
        using DevicePoseOutputRead firstRead = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(7, firstRead.Output.Number); Assert.Equal(1.1, firstRead.Output.Time, 8);

        simulation.Schedule(8, 1.1, .1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(actor[1]).Position.Y, .94442f, .94447f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(actor[1]).Linear.Y, -.2225f, -.2220f);
        Assert.InRange(sources.ReadModels(target)[1].Translation.Y, .94442f, .94447f);
        Assert.InRange(Vector3.Distance(new(.1999229f, .94999784f, 0), sources.ReadModels(target)[2].Translation), 0, .001f);
        using DevicePoseOutputRead secondRead = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(1ul, secondRead.Output.Input.InputNumber); Assert.Equal(1.1, secondRead.Output.Input.CalculationTime, 8);
        Assert.Equal(8, secondRead.Output.Number); Assert.Equal(1.2, secondRead.Output.Time, 8);
          
         
    }
    private readonly struct SeedPoseBodyContact(ContactManifolds<DeviceAvbdContactState> contacts, Entity floor, Entity body) : IJob
    {
        public void Execute()
        {
            contacts.Resize(1, 1, 2); contacts.InitializeState(0);
            ContactColumns columns = contacts.Columns;
            columns.Pairs[0] = new(floor, body, 0, 0, 0, 1);
            columns.SetPoint(0, new ContactPoint { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 });
            columns.BodyCounts[0] = 2;
            columns.BodyLinks[0] = new(floor, Vector3.Zero, 1); columns.BodyLinks[1] = new(body, Vector3.Zero, 1);
            columns.Materials[0] = new() { NormalStiffness = 100 };
            columns.RebuildBodyPairs();
        }
    }

}
