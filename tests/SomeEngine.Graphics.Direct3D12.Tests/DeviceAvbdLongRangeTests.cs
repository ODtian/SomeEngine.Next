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
    public void DeviceJointBoundsReduceTheAnchoredChainEndTravelInOneRound()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 7);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        using var frames = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 6, "DeviceJoints");
        using var frameStates = new DeviceConstraintState<DeviceAvbdJointState>(backend, device, 6, "DeviceAvbdJointStates");
        using var sets = new DeviceConstraintData<DeviceJointBoundSet>(backend, device, 6, "DeviceJointBoundSets");
        Entity root = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static), previous = root;
        Entity last = default;
        Entity[] jointEntities = new Entity[6];
        for (int index = 0; index < 6; index++)
        {
            last = CreateBody(world, bodies, equations, index + 1, new(0, -2 * index - 1, 0), BodyMotion.Dynamic);
            world.Replace(last, CreateChainLinkMass());
            var joint = world.CreateRelation(previous, last, JointConstraints.CreateBall(
                new(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity),
                new(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
            world.Add(joint.Entity, new AvbdJointState { LinearPenalty = new(100) });
            world.AddBuffer<LongRangeJointBound>(joint.Entity);
            world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointConstraint>(frames, index));
            world.Add(joint.Entity, new DeviceAvbdState<DeviceAvbdJointState>(frameStates));
            world.Add(joint.Entity, new DeviceConstraintBinding<DeviceJointBoundSet>(sets, index));
            jointEntities[index] = joint.Entity; previous = last;
        }
        Entity referencePrevious = CreateReferenceChainBody(reference, Vector3.Zero, BodyMotion.Static), referenceLast = default;
        for (int index = 0; index < 6; index++)
        {
            referenceLast = CreateReferenceChainBody(reference, new(0, -2 * index - 1, 0), BodyMotion.Dynamic);
            var joint = reference.CreateRelation(referencePrevious, referenceLast, JointConstraints.CreateBall(
                new(index == 0 ? Vector3.Zero : -Vector3.UnitY, Quaternion.Identity), new(Vector3.UnitY, Quaternion.Identity), float.PositiveInfinity));
            reference.Add(joint.Entity, new AvbdJointState { LinearPenalty = new(100) }); referencePrevious = referenceLast;
        }
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle massRows = world.Query(new QueryDefinitionBuilder().Read<BodyMass>());
        QueryHandle joints = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>());
        QueryHandle jointStates = world.Query(new QueryDefinitionBuilder().Read<AvbdJointState>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().Read<DeviceAvbdState<DeviceAvbdJointState>>());
        QueryHandle boundRows = world.Query(new QueryDefinitionBuilder().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Buffer<LongRangeJointBound>(QueryAccess.ReadWrite));
        using var bounds = new DeviceJointBounds(backend, device, world, massRows, boundRows, frames, sets, 16, new(4, 32, .99f, .99f));
        foreach (Entity entity in jointEntities) world.Add(entity, new DeviceJointBoundsBinding(bounds));
        using var frameInputs = frames.WriteInputs<JointConstraint, DeviceJointSource>(world, joints, default, 64);
        using var boundInputs = bounds.WriteInputs(64);
        using var stateInputs = new DeviceAvbdStateInputs<AvbdJointState, DeviceJointConstraint, DeviceAvbdJointState, DeviceAvbdJointStateSource>(
            backend, device, world, jointStates, frames, frameStates, default);
        using var selected = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint>(backend, device, world, joints, frames, "DeviceJointsSelection");
        using var selectedBounds = new DeviceConstraintSelection<JointConstraint, DeviceJointBoundSet, DeviceJointBoundsRows>(
            backend, device, world, boundRows, sets, new(bounds), "DeviceJointBoundSetsSelection");
        using var selectedStates = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceAvbdStateRows<DeviceAvbdJointState>>(
            backend, device, world, joints, frames, new(frameStates), "DeviceJointAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        frames.AddBindings(bindings); frameStates.AddBindings(bindings); sets.AddBindings(bindings); bounds.AddBindings(bindings);
        JobResourceAccess[] reads = [.. frames.GetReadAccesses(jobs), .. sets.GetReadAccesses(jobs), .. bounds.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileJointBoundsGraph(),
            new(frames.Capacity + bounds.Capacity, 2), bindings, reads, 64, new IDeviceConstraintSelection[] { selected, selectedBounds });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileJointBoundsAvbd(), new(1, 1, 0, 0), bindings,
            frames.GetReadAccesses(jobs).Concat(sets.GetReadAccesses(jobs)).Concat(bounds.GetWriteAccesses(jobs)).Append(frameStates.GetWriteAccess(jobs)).ToArray(),
            64, new[] { selectedStates });
        var gpu = bounds.BuildInputs().Then(frames.BeginInputs()).Then(frameInputs).Then(frames.UploadInputs()).Then(stateInputs.Upload())
            .Then(sets.BeginInputs()).Then(boundInputs).Then(sets.UploadInputs()).Then(bounds.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        QueryHandle referenceBodies = reference.Query(new QueryDefinitionBuilder().ReadWrite<BodyPose>().Read<BodyMass>()
            .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
        QueryHandle referenceJoints = reference.Query(new QueryDefinitionBuilder().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<AvbdJointState>());
        var cpu = new PredictBodies(new(0, -10, 0), 64)
            .Then(SolveAvbd.Create(reference, referenceBodies, new AvbdConstraints<JointConstraint, AvbdJointMath>(referenceJoints), 1, 0, 0))
            .Then(new UpdateBodyVelocities(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, cpu, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, gpu, 1).Schedule(0, 0, .1).Complete();

        float referenceTravel = MathF.Abs(reference.Read<BodyPose>(referenceLast).Position.Y + 11);
        Assert.InRange(MathF.Abs(world.Read<BodyPose>(last).Position.Y + 11), 0, referenceTravel * .5f);
        Assert.InRange(MathF.Abs(world.Read<BodyVelocity>(last).Linear.Y), 0, MathF.Abs(reference.Read<BodyVelocity>(referenceLast).Linear.Y) * .5f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(root).Position);
        Assert.InRange(world.Read<BodyVelocity>(last).Angular.Length(), .001f, 2);
        Assert.Equal(.1, world.Read<BodyStepState>(last).Time);
        reference.ReleaseQuery(referenceJoints); reference.ReleaseQuery(referenceBodies);
        world.ReleaseQuery(boundRows); world.ReleaseQuery(jointStates); world.ReleaseQuery(joints);
        world.ReleaseQuery(massRows); world.ReleaseQuery(members);
    }
    private static Entity CreateReferenceChainBody(World world, Vector3 position, BodyMotion motion)
    {
        Entity body = world.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, motion == BodyMotion.Dynamic ? CreateChainLinkMass() : new BodyMass { Motion = motion });
        world.Add(body, new BodyVelocity()); world.Add(body, new BodyStepState()); world.Add(body, new AvbdBodyEquation());
        return body;
    }
    private static BodyMass CreateChainLinkMass() => new()
    {
        Motion = BodyMotion.Dynamic, InverseMass = 1, CenterOfMass = new(.25f, 0, .2f),
        InverseInertia = new(1, .1f, .25f, 0, .1f, .8f, -.05f, 0, .25f, -.05f, 1.2f, 0, 0, 0, 0, 1),
    };
    private DeviceGraphKernels CompileJointBoundsGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_joint_bound_graph", """
            #include "physics_joint_bodies.slang"
            #include "physics_joint_bound_bodies.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceConstraintBodyGroups<DeviceJointBodies,DeviceJointBoundBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileJointBoundsAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("avbd_joint_bounds", """
            #include "physics_avbd_joint.slang"
            #include "physics_avbd_joint_bounds.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceJointAvbdConstraints,DeviceJointBoundAvbdConstraints> rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceJointAvbdConstraints,DeviceJointBoundAvbdConstraints> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceJointAvbdConstraints,DeviceJointBoundAvbdConstraints> rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
}
