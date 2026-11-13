using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
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
    public void DynamicCapsuleContactsAndInternalLengthsCompleteTheirSharedPoseFrameAfterAnOlderDeviceRead()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var skeleton = new Skeleton([-1, 0], [Bone(Vector3.UnitY), Bone(new(1, -.2f, 0))], [1, 2]);
        using SkeletonInstance instance = new PoseStorage().CreateInstance(skeleton);
        var clip = QuantizedClipData.Encode(new AnimationClip(skeleton, 1,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(1, []))), 4);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(clip, Loop: true));
        Entity target = GpuPoseAnimation.CreateTarget(world, instance);
        GpuPoseTarget input = world.ReadComponentData<GpuPoseTarget>(target);
        input.Blend = blend; input.Rig = new(new GpuXpbdPose(blend,
            new(skeleton, [new(0), new(1)], new(0, -6, 0), damping: 0, step: .1f, iterations: 1)));
        input.Playing = true; input.InputNumber = 1;
        world.Set(target, input);
        Query targets = world.Query(new QueryDescription().WithStored<GpuPoseTarget>());
        using var poseBodies = new DevicePosePhysics(backend, device, world, new(3, 1, 1));
        poseBodies.Bind(target);
        Entity[] actor = poseBodies.GetBodies(target, 0).ToArray();
        using var equations = new DeviceAvbdBodyEquations(backend, device, poseBodies.Bodies);
        for (int index = 0; index < actor.Length; index++) world.Add(actor[index], new DeviceAvbdEquationBinding(equations, index));
        Entity floor = CreateBody(world, poseBodies.Bodies, equations, 2, Vector3.Zero, BodyMotion.Static);
        world.Add(floor, new BoxGeometry(new(4, .75f, 4)));
        Entity capsule = world.Create(new DynamicCapsule(new(actor[0], Vector3.Zero), new(actor[1], Vector3.Zero), .05f));
        foreach (Entity shape in new[] { floor, capsule }) world.Add(shape, new ContactMaterial { NormalStiffness = float.PositiveInfinity });
        using var sources = new DevicePoseSources(backend, device, world, targets,
            DevicePoseShaders.Get(device.Capabilities.CompileTarget), new(1, 2, 1, 2, 1, 0, 1, 1), poseBodies);
        var poseStages = sources.CreateBodyStages(DevicePoseShaders.GetBodies(device.Capabilities.CompileTarget));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().WithStored<DeviceAvbdEquationBinding>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>()
            .WithStored<DeviceConstraintBinding<DeviceLengthConstraint>>().WithStored<DeviceXpbdState<float>>());
        Query boxes = world.Query(new QueryDescription().WithStored<BoxGeometry>());
        Query capsules = world.Query(new QueryDescription().WithStored<DynamicCapsule>());
        world.Add(actor[1], new ReportContacts());
        using var bodyInputs = new DeviceBodyInputs(world, members, poseBodies.Bodies);
        using var activities = new DeviceBodyActivities(backend, device, world, members, poseBodies.Bodies);
        foreach (Entity body in new[] { actor[0], actor[1], floor })
        { world.Add(body, new BodyActivity { Radius = 1 }); world.Add(body, new DeviceBodyActivityBinding(activities)); }
        var activityBindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(activityBindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, poseBodies.Bodies,
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
            new(4, 4, 1) { Contacts = 4, BodiesPerContact = 3 }, new(1e-5f, 32, 64), .02f, DeviceInputTransfer.Resident, DeviceCollisionReadback.None);
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(4, 4, 12),
            new() { NormalPenalty = 1e4f });
        using var matching = new DeviceContactMatches<DeviceAvbdContactState>(backend, device, contacts, detection, poseBodies.Bodies,
            CompileContactMatching(), new(.02f, .99f, true), new() { NormalStiffness = float.PositiveInfinity }, 17);
        using var stateInputs = new ContactStateInitialization<DeviceAvbdContactState>(backend, device, matching, CompileHistoryStateInitialization());
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengths, poseBodies.Lengths, new(poseBodies.LengthMultipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>(activityBindings);
        contacts.Current.AddBindings(bindings);
        poseBodies.Lengths.AddBindings(bindings); poseBodies.LengthMultipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. poseBodies.Lengths.GetReadAccesses(jobs), .. activities.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, poseBodies.Bodies, dynamics, CompilePoseContactGraph(), new(5, 3),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedLengths }, contacts: contacts);
        JobResourceAccess[] solveAccesses = [.. reads, poseBodies.LengthMultipliers.GetReadAccess(jobs)];
        using var avbd = new AvbdGraphCommands(backend, device, poseBodies.Bodies, equations, graph, CompileComplementaryAvbd(), new(1, 1, 1, 0),
            bindings, solveAccesses,
            64);
        using var xpbd = new DeviceXpbdProjection<DeviceLengthConstraint, float>(backend, device, world, lengths,
            poseBodies.Bodies, dynamics, poseBodies.Lengths, poseBodies.LengthMultipliers, CompileComplementaryLength(), 64);
        using var response = new DeviceContactActions(backend, device, poseBodies.Bodies, graph, contacts, CompileContactActions(), bindings,
            reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompilePoseContactActivity(), new(.05f, 10, .001f, .001f), bindings, reads, 64);
        using var samples = new DeviceContactSamples(backend, device, poseBodies.Bodies, contacts, CompileResponseContactSamples());
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        using var output = new PhysicsEventBuffer(world, 8);
        using var contactEvents = new DeviceContactEvents(world, samples, contactResults.Contacts, output, .001f);
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var eventConsumer = new RecordPhysicsEvents(jobs, output.Watch(), received);
        using var results = new DeviceBodyResults(backend, device, poseBodies.Bodies);
        var initial = new PhysicsStepContext(world, world.Query(new QueryDescription()), 6, 0, 1, 0, 0);
        poseBodies.Upload().Then(bodyInputs.Upload())
            .Then(sources.Evaluate()).Then(sources.CopyOutput()).Schedule(in initial, default).Complete();
        using DevicePoseOutputRead held = GpuPoseAnimation.ReadDeviceOutput(world, target);
        var stages = poseBodies.Upload().Then(bodyInputs.Upload()).Then(poseStages.WriteTargets())
            .Then(activity.UploadInputs()).Then(dynamics.UpdateSelection()).Then(activity.WakeConnected())
            .Then(dynamics.Predict(new(0, -6, 0)))
            .Then(grid.BeginInputs()).Then(boxInputs).Then(capsuleInputs).Then(boxUpdate.Update()).Then(capsuleUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
            .Then(detection.Detect()).Then(matching.Match()).Then(stateInputs.Initialize())
            .Then(activity.WakeConnected()).Then(dynamics.Resume(new(0, -6, 0)))
            .Then(graph.Build()).Then(response.Initialize()).Then(new InitializeMappedPoseEquations(avbd)).Then(xpbd.Initialize())
            .Then(new AlternateContactAndLength(avbd.Iterate(1, buildGraph: false), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(response.ApplyRestitution(1, buildGraph: false))
            .Then(activity.Finish()).Then(activity.Readback(output)).Then(samples.Readback()).Then(contactResults.Readback(contacts)).Then(contactEvents).Then(output.Emit())
            .Then(poseStages.MapBones()).Then(sources.CopyOutput());
        var consumer = results.Readback(64).Then(sources.Readback()).Then(eventConsumer);
        using var frame = PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).CreateFrame(consumer, .1, .3, 7, 0);

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
        Assert.InRange(Vector3.Distance(new(1, .8f, 0), world.ReadComponentData<BodyPose>(actor[1]).Position), 0, .002f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(actor[1]).Linear.Length(), 0, .002f);
        Assert.InRange(Vector3.Distance(new(1, .8f, 0), sources.ReadModels(target)[1].Translation), 0, .002f);
        Assert.Equal(8, world.ReadComponentData<BodyStepState>(actor[1]).Number); Assert.Equal(.2, world.ReadComponentData<BodyStepState>(actor[1]).Time, 8);
        Assert.Equal(new[] { PhysicsEventPhase.Begin, PhysicsEventPhase.Stay }, received.Select(value => value.Event.Phase));
        Assert.Equal(new long[] { 7, 8 }, received.Select(value => value.Number));
        Assert.Equal((received[0].Event.First, received[0].Event.Second, received[0].Event.FirstShape, received[0].Event.SecondShape),
            (received[1].Event.First, received[1].Event.Second, received[1].Event.FirstShape, received[1].Event.SecondShape));
        Assert.All(received, value => { Assert.Equal(floor, value.Event.First); Assert.Equal(capsule, value.Event.Second);
            Assert.InRange(value.Event.Force.Y, 5.95f, 6.05f); });
        using DevicePoseOutputRead current = GpuPoseAnimation.ReadDeviceOutput(world, target);
        Assert.Equal(8, current.Output.Number); Assert.Equal(.2, current.Output.Time, 8);
        Assert.Equal(1ul, current.Output.Input.InputNumber); Assert.Equal(0, current.Output.Input.CalculationTime);
           
            
    }

    private readonly struct InitializeMappedPoseEquations(AvbdGraphCommands solver) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
            solver.Initialize(context.Number == 7 ? DeviceInputTransfer.Upload : DeviceInputTransfer.Resident)
                .Schedule(in context, dependency);
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
                new("FinishDeviceBodyActivity", SlangStage.Compute), new("ReadRemovedDeviceActivityContacts", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4), Entry(shader, 5), Entry(shader, 6), RemovedContacts: Entry(shader, 7));
    }
}
