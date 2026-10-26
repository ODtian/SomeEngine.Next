using System.Numerics;
using SomeEngine.Animation.Pose;
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
    public void HeldGameSourceFeedsTheCommonBodyStepAndMapsItsCompletedBonesBeforeTheAimStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
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
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuAimPose(new GpuXpbdPose(blend, chain), aim, 0));
        input.Playing = true; input.InputNumber = 1; input.CalculationTime = 1.1; input.PlaybackTime = .1f; input.SampleTime = .1f;
        world.Replace(target, input);
        world.AddBuffer<GpuPoseGoal>(target);
        world.ExecuteBufferWrite<GpuPoseGoal>(target, goals => goals.Add(new() { Position = new(2, 1, 0), PositionWeight = 1 }));
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());
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
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        using var bodyInputs = new DeviceBodyInputs(world, members, poseBodies.Bodies);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, poseBodies.Bodies, CompileDynamics(), 64);
        Entity point = world.CreateRelation(floor, actor[1], new ContactPoint
            { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 }).Entity;
        var first = world.CreateRelation(point, floor, new ContactParticipant());
        var second = world.CreateRelation(point, actor[1], new ContactParticipant());
        world.AddBuffer<ContactBodyLink>(point);
        world.ExecuteBufferWrite<ContactBodyLink>(point, links =>
        { links.Add(new(first, Vector3.Zero, 1)); links.Add(new(second, Vector3.Zero, 1)); });
        world.Add(point, new ContactMaterial { NormalStiffness = 100 });
        world.Add(point, new ContactResponse()); world.Add(point, new AvbdContactState { NormalPenalty = 900 });
        using var contacts = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 1, "DeviceContacts");
        using var ends = new DeviceContactBodies(backend, device, 1, 2);
        using var contactStates = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 1, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 1, "DeviceContactResponses");
        world.Add(point, new DeviceConstraintBinding<DeviceContactConstraint>(contacts, 0));
        world.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(contactStates));
        world.Add(point, new DeviceContactResponseBinding(responses));
        QueryHandle contactRows = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().Read<ContactMaterial>().ReadBuffer<ContactBodyLink>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        using var writeContacts = contacts.WriteInputs<ContactPoint, DeviceContactSource>(world, contactRows, new(ends), 64,
            [ends.GetUploadAccess(jobs)]);
        using var writeStates = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState,
            DeviceAvbdContactStateSource>(backend, device, world, stateRows, contacts, contactStates, default);
        using var selectedContacts = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device,
            world, contactRows, contacts, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, contactRows, contacts, new(contactStates), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, contactRows, contacts, new(responses), "DeviceContactResponsesSelection");
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengths, poseBodies.Lengths, new(poseBodies.LengthMultipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.AddBindings(bindings); ends.AddBindings(bindings); contactStates.AddBindings(bindings); responses.AddBindings(bindings);
        poseBodies.Lengths.AddBindings(bindings); poseBodies.LengthMultipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. contacts.GetReadAccesses(jobs), ends.GetReadAccess(jobs), .. poseBodies.Lengths.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, poseBodies.Bodies, dynamics, CompileComplementaryGraph(), new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedContacts, selectedLengths });
        JobResourceAccess[] solveAccesses = [.. reads, contactStates.GetWriteAccess(jobs), responses.GetWriteAccess(jobs), poseBodies.LengthMultipliers.GetReadAccess(jobs)];
        using var avbd = new DeviceAvbdSolver(backend, device, poseBodies.Bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 0, 0),
            bindings, solveAccesses, 64, new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengths,
            poseBodies.Bodies, dynamics, poseBodies.Lengths, poseBodies.LengthMultipliers, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, poseBodies.Bodies, graph, responses, CompileContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var results = new DeviceBodyResults(backend, device, poseBodies.Bodies);
        var stages = poseBodies.Upload().Then(bodyInputs.Upload()).Then(poseStages.WriteTargets())
            .Then(contacts.BeginInputs()).Then(writeContacts).Then(contacts.UploadInputs()).Then(ends.UploadInputs()).Then(writeStates.Upload())
            .Then(dynamics.Predict(chain.Acceleration)).Then(response.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(poseStages.MapBones()).Then(results.Readback(64)).Then(sources.Readback()).Then(sources.CopyOutput());
        var simulation = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1);

        simulation.Schedule(7, 1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(actor[1]).Position.Y, .96665f, .96668f);
        Assert.InRange(sources.ReadModels(target)[1].Translation.Y, .96665f, .96668f);
        Assert.InRange(Vector3.Distance(new(.19997223f, .9699996f, 0), sources.ReadModels(target)[2].Translation), 0, .001f);
        using DevicePoseOutputRead firstRead = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(7, firstRead.Output.Number); Assert.Equal(1.1, firstRead.Output.Time, 8);

        simulation.Schedule(8, 1.1, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(actor[1]).Position.Y, .94442f, .94447f);
        Assert.InRange(world.Read<BodyVelocity>(actor[1]).Linear.Y, -.2225f, -.2220f);
        Assert.InRange(sources.ReadModels(target)[1].Translation.Y, .94442f, .94447f);
        Assert.InRange(Vector3.Distance(new(.1999229f, .94999784f, 0), sources.ReadModels(target)[2].Translation), 0, .001f);
        using DevicePoseOutputRead secondRead = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(1ul, secondRead.Output.Input.InputNumber); Assert.Equal(1.1, secondRead.Output.Input.CalculationTime, 8);
        Assert.Equal(8, secondRead.Output.Number); Assert.Equal(1.2, secondRead.Output.Time, 8);
        world.ReleaseQuery(stateRows); world.ReleaseQuery(contactRows); world.ReleaseQuery(lengths);
        world.ReleaseQuery(members); world.ReleaseQuery(targets);
    }
}
