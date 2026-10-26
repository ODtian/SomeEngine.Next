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

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void TheFrameBreaksADeviceJointFromItsCurrentBodyForceAndReleasesTheNextStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitX, BodyMotion.Dynamic);
        using var parameters = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 4, "DeviceJoints");
        using var histories = new DeviceConstraintState<DeviceAvbdJointState>(backend, device, 4, "DeviceAvbdJointStates");
        Entity joint = world.CreateRelation(anchor, moving, JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 100)).Entity;
        world.Add(joint, new AvbdJointState { LinearPenalty = new(900), AngularPenalty = new(900) });
        world.Add(joint, new DeviceConstraintBinding<DeviceJointConstraint>(parameters, 3));
        world.Add(joint, new DeviceAvbdState<DeviceAvbdJointState>(histories));
        world.Add(joint, new ConstraintBreakSettings(40, float.PositiveInfinity));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle joints = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle states = world.Query(world.QueryDefinition().Read<AvbdJointState>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().Read<DeviceAvbdState<DeviceAvbdJointState>>());
        QueryHandle breakingRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<ConstraintBreakSettings>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().Read<DeviceConstraintForceBinding<DeviceJointConstraint>>());
        QueryHandle events = world.Query(world.QueryDefinition().Read<PhysicsStepEvents>().ReadBuffer<PhysicsEvent>());
        using var input = parameters.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var stateInput = new DeviceAvbdStateInputs<AvbdJointState, DeviceJointConstraint, DeviceAvbdJointState, DeviceAvbdJointStateSource>(
            backend, device, world, states, parameters, histories, default);
        using var selected = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint>(backend, device, world, joints,
            parameters, "DeviceJointsSelection");
        using var selectedStates = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceAvbdStateRows<DeviceAvbdJointState>>(
            backend, device, world, joints, parameters, new(histories), "DeviceJointAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var bodyResults = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>(); parameters.AddBindings(bindings); histories.AddBindings(bindings);
        JobResourceAccess[] reads = [.. parameters.GetReadAccesses(jobs), histories.GetReadAccess(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileJointGraph(), new(4, 2), bindings, reads, 64, new[] { selected });
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileJointAvbd(), new(32, 1, 0, 0),
            bindings, parameters.GetReadAccesses(jobs).Append(histories.GetWriteAccess(jobs)).ToArray(), 64, new[] { selectedStates });
        using var forces = new DeviceConstraintForceSamples<DeviceJointConstraint>(backend, device, bodies, parameters,
            CompileJointForceSamples(), 2, bindings, reads, 64, new IDeviceConstraintSelection[] { selected, selectedStates });
        world.Add(joint, new DeviceConstraintForceBinding<DeviceJointConstraint>(forces));
        var descriptions = new QueryReadResources();
        descriptions.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        descriptions.Add<DeviceConstraintBinding<DeviceJointConstraint>, DeviceConstraintReferenceResources<DeviceJointConstraint>>();
        descriptions.Add<DeviceConstraintForceBinding<DeviceJointConstraint>, DeviceConstraintForceResources<DeviceJointConstraint>>();
        using var output = new PhysicsEventBuffer(jobs, 4);
        using var breaking = new BreakConstraints<JointConstraint, DeviceConstraintForces<JointConstraint, DeviceJointConstraint>>(
            world, members, breakingRows, output, descriptions, forces.GetReadAccesses(jobs));
        var received = new List<(long Number, PhysicsEvent Event)>();
        using var consumer = new ConsumePhysicsEvents<RecordContactNotifications>(world, events, new(received),
            [JobResourceAccess.Write(jobs.GetResourceToken(received))]);
        var stages = bodyInputs.Upload().Then(parameters.BeginInputs()).Then(input).Then(parameters.UploadInputs()).Then(stateInput.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(forces.Readback())
            .Then(breaking).Then(bodyResults.Readback(64)).Then(output.Emit());
        using var frame = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).CreateFrame(consumer, .1, 0, 0, 0);

        frame.AdvanceTime(.1); frame.CompleteForRead();

        var broken = Assert.Single(received);
        Assert.Equal(PhysicsEventKind.Break, broken.Event.Kind); Assert.Equal(joint, broken.Event.Subject);
        Assert.Equal(anchor, broken.Event.Body); Assert.Equal(anchor, broken.Event.First); Assert.Equal(moving, broken.Event.Second);
        Assert.Equal(0, broken.Number); Assert.Equal(.1, broken.Event.Time);
        Assert.InRange(broken.Event.Force.X, 49.999f, 50.001f); Assert.Equal(Vector3.Zero, broken.Event.Torque);
        Assert.Equal(0, world.GetOutgoingRelations<JointConstraint>(anchor).Count);
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, .49999f, .50001f);
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.InRange(world.Read<BodyPose>(moving).Position.X, -.00002f, .00002f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, -5.0001f, -4.9999f);
        world.ReleaseQuery(events); world.ReleaseQuery(breakingRows); world.ReleaseQuery(states);
        world.ReleaseQuery(joints); world.ReleaseQuery(members);
    }

    private ComputePipelineDesc CompileJointForceSamples()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("joint_force_samples", """
            #include "physics_avbd_joint_forces.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadForces(uint3 id:SV_DispatchThreadID)
            { DeviceJointAvbdForces rows; readDeviceConstraintForces(id.x,rows); }
            """, [new("ReadForces", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0));
    }
}
