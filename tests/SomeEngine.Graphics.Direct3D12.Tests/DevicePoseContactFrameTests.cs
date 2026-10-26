using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
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
    public void DynamicCapsuleContactsAndInternalLengthsCompleteTheirSharedPoseFrameAfterAnOlderDeviceRead()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(Vector3.UnitY), Bone(new(1, -.2f, 0))], [1, 2]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        Entity target = GpuPoseAnimation.CreateTarget(world, instance);
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuXpbdPose(blend,
            new(skeleton, [new(0), new(1)], new(0, -6, 0), damping: 0, step: .1f, iterations: 1)));
        input.Playing = true; input.InputNumber = 1;
        world.Replace(target, input);
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());
        using var poseBodies = new DevicePosePhysics(backend, device, world, new(3, 1, 1));
        poseBodies.Bind(target);
        Entity[] actor = poseBodies.GetBodies(target, 0).ToArray();
        using var equations = new DeviceAvbdBodyEquations(backend, device, poseBodies.Bodies);
        for (int index = 0; index < actor.Length; index++) world.Add(actor[index], new DeviceAvbdEquationBinding(equations, index));
        Entity floor = CreateBody(world, poseBodies.Bodies, equations, 2, Vector3.Zero, BodyMotion.Static);
        world.Add(floor, new BoxGeometry(new(4, .75f, 4)));
        Entity capsule = world.CreateEntity(new DynamicCapsule(new(actor[0], Vector3.Zero), new(actor[1], Vector3.Zero), .05f));
        foreach (Entity shape in new[] { floor, capsule }) world.Add(shape, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        using var sources = new DevicePoseSources(backend, device, world, targets,
            DevicePoseShaders.Get(device.Capabilities.CompileTarget), new(1, 2, 1, 2, 1, 0, 1, 1), poseBodies);
        var poseStages = sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle lengths = world.Query(new QueryDefinitionBuilder().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        QueryHandle boxes = world.Query(new QueryDefinitionBuilder().Read<BoxGeometry>());
        QueryHandle capsules = world.Query(new QueryDefinitionBuilder().Read<DynamicCapsule>());
        QueryHandle poses = world.Query(new QueryDefinitionBuilder().Read<BodyPose>());
        QueryHandle points = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>());
        QueryHandle stateRows = world.Query(new QueryDefinitionBuilder().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>());
        QueryHandle events = world.Query(new QueryDefinitionBuilder().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var bodyInputs = new DeviceBodyInputs(world, members, poseBodies.Bodies);
        using var activities = new DeviceBodyActivities(backend, device, world, members, poseBodies.Bodies);
        foreach (Entity body in new[] { actor[0], actor[1], floor })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var activityBindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(activityBindings);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, poseBodies.Bodies,
            CompilePoseBodyDynamics(), 64, activityBindings, activities.GetWriteAccesses(jobs));
        using var grid = new DeviceCollisionGrid(backend, device, CompileHistoryGrid(), new(2, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), poseBodies.Bodies);
        world.Add(floor, new DeviceCollisionBody(grid, 0)); world.Add(floor, new DeviceGeometryBinding(geometry, 0));
        world.Add(capsule, new DeviceCollisionBody(grid, 1)); world.Add(capsule, new DeviceGeometryBinding(geometry, 1));
        var pairs = world.Between(boxes, capsules);
        using var boxInputs = grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(pairs, default, 0, 64);
        using var capsuleInputs = grid.WriteGeometryReferences<DynamicCapsule, DeviceDynamicCapsuleSource>(pairs, default, 1, 64);
        using var boxUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompilePoseGeometryUpdate("DeviceBoxUpdate"), 0, DeviceInputTransfer.Upload);
        using var capsuleUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry,
            CompilePoseGeometryUpdate("DeviceDynamicCapsuleUpdate"), 1, DeviceInputTransfer.Resident);
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry, CompilePoseContactDetection(), 1,
            new(4, 4, 1), new(1e-5f, 32, 64), .02f, DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts);
        using var detected = new ContactPointBuffer(jobs, 4, 12);
        using var parameters = new DeviceConstraintData<DeviceContactConstraint>(backend, device, 4, "DeviceContacts");
        using var anchors = new DeviceContactBodies(backend, device, 4, 3);
        using var histories = new DeviceConstraintState<DeviceAvbdContactState>(backend, device, 4, "DeviceAvbdContactStates");
        using var responses = new DeviceConstraintState<DeviceContactResponseState>(backend, device, 4, "DeviceContactResponses");
        using var matching = new DeviceContactMatches(backend, device, world, points, parameters, detection,
            CompileContactMatching(), new(.02f, .99f, true), 17, 64);
        using var currentGeometry = matching.CreateGeometryUpdate(poseBodies.Bodies, anchors, CompileCurrentContactGeometry());
        using var refresh = ContactPoints.Create(world, poses, points, detected, new AverageContactMaterials(),
            matching.Components(new ContactEventComponents<ContactNotifications, DeviceHistoryComponents>(new(histories, responses))),
            new(.02f, .99f, true));
        using var contactInputs = new DeviceContactInputs(world, points, parameters, anchors);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, stateRows, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, parameters, "DeviceContactsSelection");
        using var selectedStates = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, parameters, new(histories), "DeviceContactAvbdSelection");
        using var selectedResponses = new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, parameters, new(responses), "DeviceContactResponsesSelection");
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengths, poseBodies.Lengths, new(poseBodies.LengthMultipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>(activityBindings);
        parameters.AddBindings(bindings); anchors.AddBindings(bindings); histories.AddBindings(bindings); responses.AddBindings(bindings);
        poseBodies.Lengths.AddBindings(bindings); poseBodies.LengthMultipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), anchors.GetReadAccess(jobs),
            .. poseBodies.Lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, poseBodies.Bodies, dynamics, CompilePoseContactGraph(), new(5, 3),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selected, selectedLengths });
        JobResourceAccess[] solveAccesses = [.. reads, histories.GetWriteAccess(jobs), responses.GetWriteAccess(jobs), poseBodies.LengthMultipliers.GetReadAccess(jobs)];
        using var avbd = new DeviceAvbdSolver(backend, device, poseBodies.Bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 1, 0),
            bindings, solveAccesses,
            64, new IDeviceConstraintSelection[] { selectedStates, selectedResponses });
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengths,
            poseBodies.Bodies, dynamics, poseBodies.Lengths, poseBodies.LengthMultipliers, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, poseBodies.Bodies, graph, responses, CompileContactActions(), bindings,
            reads, 64, new[] { selectedResponses });
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompilePoseContactActivity(), new(.05f, 10, .001f, .001f), bindings, reads, 64);
        JobResourceAccess[] sampleAccesses = [.. reads, responses.GetReadAccess(jobs)];
        using var samples = new DeviceContactSamples(backend, device, poseBodies.Bodies, parameters, CompileResponseContactSamples(), bindings,
            sampleAccesses, 64, new IDeviceConstraintSelection[] { selected, selectedResponses });
        using var output = new PhysicsEventBuffer(jobs, 8);
        using var contactEvents = new ContactEvents<ContactNotifications, DeviceContactEventSource>(world, members, points, output, .001f, new(samples));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var eventConsumer = new ConsumePhysicsEvents<RecordContactNotifications>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        using var results = new DeviceBodyResults(backend, device, poseBodies.Bodies);
        using var contactResults = new DeviceContactResults(backend, device, parameters, responses);
        var initial = new PhysicsStepContext(world, QueryDefinition.Empty, 6, 0, 1, 0, 0);
        parameters.BeginInputs().Then(parameters.UploadInputs()).Then(poseBodies.Upload()).Then(bodyInputs.Upload())
            .Then(sources.Evaluate()).Then(sources.CopyOutput()).Schedule(in initial, default).Complete();
        using DevicePoseOutputRead held = GpuPoseAnimation.ReadDeviceOutput(world, target);
        var stages = poseBodies.Upload().Then(bodyInputs.Upload()).Then(poseStages.WriteTargets())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(0, -6, 0)))
            .Then(grid.BeginInputs()).Then(boxInputs).Then(capsuleInputs).Then(boxUpdate.Update()).Then(capsuleUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(new ClearPoseContacts(detected)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess }))
            .Then(matching.Match()).Then(refresh.Refresh(currentGeometry.Results)).Then(contactInputs.Upload()).Then(stateInputs.Upload())
            .Then(currentGeometry.Update()).Then(activity.WakeConnected()).Then(dynamics.Resume(new(0, -6, 0)))
            .Then(graph.Build()).Then(response.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1, buildGraph: false), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(samples.Readback()).Then(contactEvents).Then(output.Emit())
            .Then(poseStages.MapBones()).Then(sources.CopyOutput());
        var consumer = results.Readback(64).Then(contactResults.Readback(64)).Then(sources.Readback()).Then(eventConsumer);
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, .3, 7, 0);

        frame.AdvanceTime(.2); frame.SubmitDueSteps(); frame.ConsumeCompleted();

        Assert.Equal(6, frame.Result.Number); Assert.Equal(0, frame.Result.EndTime);
        using var older = backend.CreateBuffer(device, new(held.Output.Models.Size, BufferUsages.CopyDestination), MemoryType.Readback);
        using var commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Read an older completed pose"));
        backend.Begin(commands, new(0, 0, 8));
        backend.CopyBuffer(commands, new(held.Output.Buffer, held.Output.Models.Offset, older, 0, held.Output.Models.Size));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion copied = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [recorded], [], []));
        held.CompleteAfter(backend, device, copied);

        PhysicsStepResult completed = frame.CompleteForRead();

        using MappedBuffer mapping = backend.Map(older, MapType.Read, BufferRange.Whole); mapping.Invalidate(mapping.Range);
        Assert.InRange(Vector3.Distance(new(1, .8f, 0), MemoryMarshal.Cast<byte, Matrix4x4>(mapping.Bytes)[1].Translation), 0, .001f);
        Assert.Equal(8, completed.Number); Assert.Equal(.2, completed.EndTime, 8);
        Assert.Equal(8, detection.Number); Assert.Equal(.2, detection.Time, 8);
        Assert.InRange(Vector3.Distance(new(1, .8f, 0), world.Read<BodyPose>(actor[1]).Position), 0, .002f);
        Assert.InRange(world.Read<BodyVelocity>(actor[1]).Linear.Length(), 0, .002f);
        Assert.InRange(Vector3.Distance(new(1, .8f, 0), sources.ReadModels(target)[1].Translation), 0, .002f);
        Assert.Equal(8, world.Read<BodyStepState>(actor[1]).Number); Assert.Equal(.2, world.Read<BodyStepState>(actor[1]).Time, 8);
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay }, received.Select(value => value.Event.Phase));
        Assert.Equal(new long[] { 7, 8 }, received.Select(value => value.Number));
        Assert.Equal(received[0].Event.Subject, received[1].Event.Subject);
        Assert.All(received, value => { Assert.Equal(floor, value.Event.First); Assert.Equal(capsule, value.Event.Second);
            Assert.InRange(value.Event.Force.Y, 5.95f, 6.05f); });
        using DevicePoseOutputRead current = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(8, current.Output.Number); Assert.Equal(.2, current.Output.Time, 8);
        Assert.Equal(1ul, current.Output.Input.InputNumber); Assert.Equal(0, current.Output.Input.CalculationTime);
        world.ReleaseQuery(events); world.ReleaseQuery(stateRows); world.ReleaseQuery(points); world.ReleaseQuery(poses);
        world.ReleaseQuery(capsules); world.ReleaseQuery(boxes); world.ReleaseQuery(lengths); world.ReleaseQuery(members); world.ReleaseQuery(targets);
    }

    private readonly struct ClearPoseContacts(ContactPointBuffer contacts) : IPhysicsStage, IJob
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            WorldJobs.GetScheduler(context.World).Schedule(this, [contacts.WriteAccess], dependency);
        public void Execute() => contacts.Clear();
    }

    private ComputePipelineDesc CompilePoseGeometryUpdate(string type)
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("pose_geometry_update", $$"""
            #include "physics_geometry_update.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Update(uint3 id:SV_DispatchThreadID) { updateDeviceGeometry<{{type}}>(id.x); }
            """, [new("Update", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }

    private ComputePipelineDesc CompilePoseContactDetection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("pose_contact_detection", """
            #include "physics_geometry_references.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id:SV_DispatchThreadID)
            { DeviceConvexCollision<DeviceBox,DeviceCapsule> collision; detectDeviceReferencedCollision(id.x,collision); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }

    private DeviceGraphKernels CompilePoseContactGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "pose_contact_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_contact_data.slang"
        #include "physics_length_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceLengthBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private DeviceBodyKernels CompilePoseBodyDynamics()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("pose_body_dynamics", "#include \"physics_body_activity_dynamics.slang\"",
            [new("PredictDeviceActiveBodies", SlangStage.Compute), new("UpdateDeviceActiveBodyVelocities", SlangStage.Compute),
                new("ResumeDeviceActiveBodies", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Resume: Entry(shader, 2));
    }

    private DeviceActivityKernels CompilePoseContactActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("pose_contact_activity", """
            #include "physics_contact_activity.slang"
            #include "physics_length_activity.slang"
            #include "physics_constraint_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceConstraintActivityGroups<DeviceContactActivity,DeviceLengthActivity> rows; readDeviceActivityRows(devicePhysicsThreadIndex(id,64),rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4), Entry(shader, 5), Entry(shader, 6));
    }
}
