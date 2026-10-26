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

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void DeviceJointAndMotorGroupsShareFramesAndAccumulateTheirOwnColorOrderedMultipliers()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var joints = new DeviceConstraintData<DeviceJointConstraint>(backend, device, 1, "DeviceJoints");
        using var motors = new DeviceConstraintData<DeviceMotorConstraint>(backend, device, 1, "DeviceMotors");
        using var jointStates = new DeviceConstraintState<XpbdJointState>(backend, device, 1, "DeviceJointMultipliers");
        using var motorStates = new DeviceConstraintState<XpbdMotorState>(backend, device, 1, "DeviceMotorMultipliers");
        Entity source = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity target = AddBody(world, bodies, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity fixedBody = reference.CreateEntity(new BodyPose { Rotation = Quaternion.Identity });
        reference.Add(fixedBody, new BodyMass { Motion = BodyMotion.Static });
        reference.Add(fixedBody, new BodyVelocity()); reference.Add(fixedBody, new BodyStepState());
        Entity moving = reference.CreateEntity(new BodyPose { Position = Vector3.UnitX, Rotation = Quaternion.Identity });
        reference.Add(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
        reference.Add(moving, new BodyVelocity()); reference.Add(moving, new BodyStepState());
        JointConstraint joint = JointConstraints.CreateBall(JointFrame.Identity, JointFrame.Identity, 100);
        var motor = new JointMotor { Space = JointMotorSpace.World, Response = JointMotorResponse.Target,
            LinearStiffness = new(300, 0, 0), LinearDamping = new(20, 0, 0), MaximumForce = new(2, 0, 0) };
        var goal = new JointMotorTarget { Frame = new(new(1.5f, 0, 0), Quaternion.Identity) };
        Entity row = world.CreateRelation(source, target, joint).Entity;
        Entity referenceRow = reference.CreateRelation(fixedBody, moving, joint).Entity;
        world.Add(row, motor); world.Add(row, goal);
        reference.Add(referenceRow, motor); reference.Add(referenceRow, goal);
        reference.Add(referenceRow, new XpbdJointState()); reference.Add(referenceRow, new XpbdMotorState());
        world.Add(row, new DeviceConstraintBinding<DeviceJointConstraint>(joints, 0));
        world.Add(row, new DeviceConstraintBinding<DeviceMotorConstraint>(motors, 0));
        world.Add(row, new DeviceXpbdState<XpbdJointState>(jointStates)); world.Add(row, new DeviceXpbdState<XpbdMotorState>(motorStates));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle jointRows = world.Query(world.QueryDefinition().Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
            .Read<DeviceConstraintBinding<DeviceJointConstraint>>().All<DeviceXpbdState<XpbdJointState>>());
        QueryHandle motorRows = world.Query(world.QueryDefinition().Read<JointMotor>().Read<JointMotorTarget>()
            .Read<DeviceConstraintBinding<DeviceMotorConstraint>>().All<DeviceXpbdState<XpbdMotorState>>());
        QueryHandle referenceBodies = reference.Query(reference.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle referenceJoints = reference.Query(reference.QueryDefinition().Read<JointConstraint>()
            .Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdJointState>());
        QueryHandle referenceMotors = reference.Query(reference.QueryDefinition().Read<JointMotor>().Read<JointMotorTarget>()
            .Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>().ReadWrite<XpbdMotorState>());
        using var scalar = SolveXpbdGraph.Create(reference, referenceBodies, XpbdGraphConstraints.Combine(
            new XpbdGraphConstraints<JointConstraint, XpbdJointProjection>(referenceJoints),
            new XpbdGraphConstraints<JointConstraint, XpbdJointMotorProjection>(referenceMotors)),
            new(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 0, BodiesPerBatch: 4, LargeIslandBodyLimit: 1, RowsPerBlock: 2, UseSimd: false), 8);
        var scalarStages = new PredictBodies(Vector3.Zero, 64).Then(scalar).Then(new UpdateBodyVelocities(64));
        using var writeJoints = joints.WriteInputs<JointConstraint, DeviceJointSource>(world, jointRows, new(), 64);
        using var writeMotors = motors.WriteInputs<JointMotor, DeviceMotorSource>(world, motorRows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var selectedJoints = new DeviceConstraintSelection<JointConstraint, DeviceJointConstraint, DeviceXpbdStateRows<XpbdJointState>>(
            backend, device, world, jointRows, joints, new(jointStates), "DeviceJointsSelection");
        using var selectedMotors = new DeviceConstraintSelection<JointMotor, DeviceMotorConstraint, DeviceXpbdStateRows<XpbdMotorState>>(
            backend, device, world, motorRows, motors, new(motorStates), "DeviceMotorsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        joints.AddBindings(bindings); motors.AddBindings(bindings); jointStates.AddBindings(bindings); motorStates.AddBindings(bindings);
        JobResourceAccess[] reads = [.. joints.GetReadAccesses(jobs), .. motors.GetReadAccesses(jobs)];
        (DeviceGraphKernels graphKernels, DeviceXpbdKernels xpbdKernels) = CompileMixedJointMotor();
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, graphKernels, new(2, 2),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedJoints, selectedMotors });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, xpbdKernels, new(8, 2), bindings, reads,
            new[] { jointStates.States, motorStates.States }, 64);
        var stages = joints.BeginInputs().Then(writeJoints).Then(joints.UploadInputs())
            .Then(motors.BeginInputs()).Then(writeMotors).Then(motors.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, scalarStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Near(reference.Read<BodyPose>(moving).Position, world.Read<BodyPose>(target).Position);
        Near(reference.Read<BodyVelocity>(moving).Linear, world.Read<BodyVelocity>(target).Linear);
        Near(new(.51f, 0, 0), world.Read<BodyPose>(target).Position);
        Near(Vector3.Zero, world.Read<BodyPose>(source).Position);
        world.ReleaseQuery(motorRows); world.ReleaseQuery(jointRows); world.ReleaseQuery(members);
        reference.ReleaseQuery(referenceMotors); reference.ReleaseQuery(referenceJoints); reference.ReleaseQuery(referenceBodies);
    }

    [Fact]
    public void DeviceTypedGroupsProjectUserLengthsAndProtectedBendsInTheSameColorOrder()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 3);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 2, "DeviceLengths");
        using var bends = new DeviceConstraintData<DeviceBendConstraint>(backend, device, 1, "DeviceBends");
        using var lengthStates = new DeviceConstraintState<float>(backend, device, 2, "DeviceLengthMultipliers");
        using var bendStates = new DeviceConstraintState<float>(backend, device, 1, "DeviceBendMultipliers");
        Vector3[] positions = [Vector3.Zero, new(1.4f, 0, 0), new(1.4f, 1.2f, 0)];
        var actual = new Entity[3]; var expected = new Entity[3];
        for (int index = 0; index < positions.Length; index++)
        {
            actual[index] = AddBody(world, bodies, index, positions[index], BodyMotion.Dynamic);
            expected[index] = reference.CreateEntity(new BodyPose { Position = positions[index], Rotation = Quaternion.Identity });
            reference.Add(expected[index], new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1 });
            reference.Add(expected[index], new BodyVelocity()); reference.Add(expected[index], new BodyStepState());
        }
        for (int index = 0; index < 2; index++)
        {
            Entity length = world.CreateRelation(actual[index], actual[index + 1], new XpbdLength { RestLength = 1, Compliance = .01f }).Entity;
            world.Add(length, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, index));
            world.Add(length, new DeviceXpbdState<float>(lengthStates)); world.Add(length, new ProjectLengthScale(1.5f));
            reference.CreateRelation(expected[index], expected[index + 1], new XpbdLength { RestLength = .75f, Compliance = .01f });
        }
        var bendDefinition = new XpbdBend { MinimumCosine = .5f, Compliance = .002f };
        Entity bend = ConstraintBodies.Create(world, bendDefinition, actual);
        ConstraintBodies.Create(reference, bendDefinition, expected);
        world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(bends, 0));
        world.Add(bend, new DeviceXpbdState<float>(bendStates));
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        QueryHandle lengthRows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<ProjectLengthScale>()
            .Read<UndirectedRelationEndpoints<XpbdLength>>().Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        QueryHandle bendRows = world.Query(ConstraintBodies.AddReads(world.QueryDefinition().Read<XpbdBend>()
            .Read<DeviceConstraintBinding<DeviceBendConstraint>>().All<DeviceXpbdState<float>>().Build()));
        QueryHandle referenceBodies = reference.Query(reference.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle referenceLengths = reference.Query(reference.QueryDefinition().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle referenceBends = reference.Query(ConstraintBodies.AddReads(reference.QueryDefinition().ReadWrite<XpbdBend>().Build()));
        using var scalar = SolveXpbdGraph.Create(reference, referenceBodies, XpbdGraphConstraints.Combine(
            new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(referenceLengths),
            new XpbdGraphConstraints<XpbdBend, XpbdBendProjection>(referenceBends)),
            new(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 0, BodiesPerBatch: 4, LargeIslandBodyLimit: 1, RowsPerBlock: 2, UseSimd: false), 6);
        var scalarStages = new PredictBodies(Vector3.Zero, 64).Then(scalar).Then(new UpdateBodyVelocities(64));
        using var writeLengths = lengths.WriteInputs<XpbdLength, ProjectLengthSource>(world, lengthRows, new(), 64);
        using var writeBends = bends.WriteInputs<XpbdBend, DeviceBendSource>(world, bendRows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var selectedLengths = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, lengthRows, lengths, new(lengthStates), "DeviceLengthsSelection");
        using var selectedBends = new DeviceConstraintSelection<XpbdBend, DeviceBendConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, bendRows, bends, new(bendStates), "DeviceBendsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        lengths.AddBindings(bindings); bends.AddBindings(bindings); lengthStates.AddBindings(bindings); bendStates.AddBindings(bindings);
        JobResourceAccess[] reads = [.. lengths.GetReadAccesses(jobs), .. bends.GetReadAccesses(jobs)];
        (DeviceGraphKernels graphKernels, DeviceXpbdKernels xpbdKernels) = CompileMixedLengthBend();
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, graphKernels, new(3, 3),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedLengths, selectedBends });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, xpbdKernels, new(6, 2), bindings, reads,
            new[] { lengthStates.States, bendStates.States }, 64);
        var stages = lengths.BeginInputs().Then(writeLengths).Then(lengths.UploadInputs())
            .Then(bends.BeginInputs()).Then(writeBends).Then(bends.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, scalarStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        for (int index = 0; index < actual.Length; index++)
        {
            Near(reference.Read<BodyPose>(expected[index]).Position, world.Read<BodyPose>(actual[index]).Position);
            Assert.InRange(Vector3.Distance(reference.Read<BodyVelocity>(expected[index]).Linear, world.Read<BodyVelocity>(actual[index]).Linear), 0, 1e-4f);
        }
        Assert.True(world.Read<BodyPose>(actual[0]).Position.Y < -.01f);
        world.ReleaseQuery(bendRows); world.ReleaseQuery(lengthRows); world.ReleaseQuery(members);
        reference.ReleaseQuery(referenceBends); reference.ReleaseQuery(referenceLengths); reference.ReleaseQuery(referenceBodies);
    }

    private (DeviceGraphKernels, DeviceXpbdKernels) CompileMixedJointMotor()
    {
        D3D12TestShader graph = shaderCompiler.CompileWithSearchPaths("xpbd_joint_motor_graph", """
            #include "physics_joint_bodies.slang"
            #include "physics_motor_bodies.slang"
            typedef DeviceConstraintBodyGroups<DeviceJointBodies, DeviceMotorBodies> BodyRows;
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { BodyRows rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_joint_motor", """
            #include "physics_xpbd_graph.slang"
            #include "physics_joint_bodies.slang"
            #include "physics_motor_bodies.slang"
            #include "physics_xpbd_joint.slang"
            #include "physics_xpbd_motor.slang"
            typedef DeviceXpbdConstraintRows<DeviceJointBodies, DeviceXpbdJointProjection<DeviceGraphBodies>> JointRows;
            typedef DeviceXpbdConstraintRows<DeviceMotorBodies, DeviceXpbdMotorProjection<DeviceGraphBodies>> MotorRows;
            typedef DeviceXpbdConstraintGroups<JointRows, MotorRows> ProjectionRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            { ProjectionRows rows; initializeDeviceXpbdGraph(group.x, thread.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            { ProjectionRows rows; projectDeviceXpbdGraph(group.x, thread.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(D3D12TestShader source, int index) => new(source.SlangCode, source.Reflection, source.GetEntryPoint(index), source.GetEntryCode(index));
        return (new(Entry(graph, 0), Entry(graph, 1)), new(Entry(shader, 0), Entry(shader, 1)));
    }

    private (DeviceGraphKernels, DeviceXpbdKernels) CompileMixedLengthBend()
    {
        D3D12TestShader graph = shaderCompiler.CompileWithSearchPaths("xpbd_mixed_length_bend_graph", """
            #include "physics_length_graph.slang"
            #include "physics_bend_graph.slang"
            typedef DeviceConstraintBodyGroups<DeviceLengthBodies, DeviceBendBodies> BodyRows;
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { BodyRows rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_mixed_length_bend", """
            #include "physics_xpbd_graph.slang"
            #include "physics_length_graph.slang"
            #include "physics_bend_graph.slang"
            #include "physics_xpbd_length.slang"
            #include "physics_xpbd_bend.slang"
            struct ProjectLength : IDeviceXpbdProjection
            {
                bool active(uint row) { return DeviceLengthsMetadata[row].active != 0; }
                void initialize(uint row, DeviceXpbdStep context) { DeviceLengthMultipliers[row] = 0; }
                void project(uint row, DeviceXpbdStep context)
                {
                    DeviceLengthConstraint data = DeviceLengths[row];
                    DeviceBodyState first = DeviceBodyStates[data.firstBody], second = DeviceBodyStates[data.secondBody];
                    DeviceGraphBodies selected;
                    float3 delta = second.position - first.position;
                    float distance = length(delta);
                    float firstWeight = first.motion == 2 && selected.contains(data.firstBody) ? first.inverseMass : 0;
                    float secondWeight = second.motion == 2 && selected.contains(data.secondBody) ? second.inverseMass : 0;
                    float alpha = data.compliance / (context.duration * context.duration);
                    float multiplier = DeviceLengthMultipliers[row];
                    float change = (-(distance - data.restLength * .5) - alpha * multiplier) / (firstWeight + secondWeight + alpha);
                    DeviceLengthMultipliers[row] = multiplier + change;
                    if (firstWeight > 0) first.position -= firstWeight * delta / distance * change;
                    if (secondWeight > 0) second.position += secondWeight * delta / distance * change;
                    DeviceBodyStates[data.firstBody] = first; DeviceBodyStates[data.secondBody] = second;
                }
            };
            typedef DeviceXpbdConstraintRows<DeviceLengthBodies, ProjectLength> LengthRows;
            typedef DeviceXpbdConstraintRows<DeviceBendBodies, DeviceXpbdBendProjection<DeviceGraphBodies>> BendRows;
            typedef DeviceXpbdConstraintGroups<LengthRows, BendRows> ProjectionRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            { ProjectionRows rows; initializeDeviceXpbdGraph(group.x, thread.x, rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            { ProjectionRows rows; projectDeviceXpbdGraph(group.x, thread.x, rows); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(D3D12TestShader source, int index) => new(source.SlangCode, source.Reflection, source.GetEntryPoint(index), source.GetEntryCode(index));
        return (new(Entry(graph, 0), Entry(graph, 1)), new(Entry(shader, 0), Entry(shader, 1)));
    }
}
