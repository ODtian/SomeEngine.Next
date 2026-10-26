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
    public void DeviceColorBlocksAndIndependentIslandsMatchTheSelectedBodyResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 13);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 10, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 10, "DeviceLengthMultipliers");
        var actual = new Entity[13]; var expected = new Entity[13];
        for (int index = 0; index < actual.Length; index++)
        {
            Vector3 position = new(index < 9 ? index * 1.2f : index < 11 ? 30 + (index - 9) * 1.2f : 40 + (index - 11) * 1.2f, 0, 0);
            actual[index] = AddBody(world, bodies, index, position, BodyMotion.Dynamic);
            if (index != 0) world.Add(actual[index], new SelectedXpbdBody());
            expected[index] = reference.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
            reference.Add(expected[index], new BodyMass { Motion = index == 0 ? BodyMotion.Static : BodyMotion.Dynamic, InverseMass = 1 });
            reference.Add(expected[index], new BodyVelocity()); reference.Add(expected[index], new BodyStepState());
        }
        world.Replace(actual[0], new BodyVelocity { Linear = new(7, 0, 0) });
        reference.Replace(expected[0], new BodyVelocity { Linear = new(7, 0, 0) });
        for (int index = 0; index < 10; index++)
        {
            int first = index < 8 ? index : index == 8 ? 9 : 11;
            var definition = new XpbdLength { RestLength = 1, Compliance = .01f };
            RelationEdge<XpbdLength> edge = world.CreateRelation(actual[first], actual[first + 1], definition);
            reference.CreateRelation(expected[first], expected[first + 1], definition);
            world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, index));
            world.Add(edge.Entity, new DeviceXpbdState<float>(multipliers));
        }
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<SelectedXpbdBody>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        QueryHandle referenceBodies = reference.Query(reference.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle referenceRows = reference.Query(reference.QueryDefinition().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var scalar = SolveXpbdGraph.Create(reference, referenceBodies,
            new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(referenceRows),
            new(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 2, BodiesPerBatch: 4, LargeIslandBodyLimit: 1, RowsPerBlock: 2, UseSimd: false), 8);
        var referenceStages = new PredictBodies(Vector3.Zero, 64).Then(scalar).Then(new UpdateBodyVelocities(64));
        using var write = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var selection = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileLengthGraph(),
            new(10, 2), bindings, lengths.GetReadAccesses(jobs), 64, new[] { selection });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileGraphLengthProjection(),
            new DeviceXpbdSettings(Iterations: 8, RowsPerBlock: 2), bindings, lengths.GetReadAccesses(jobs),
            new[] { multipliers.States }, threadsPerGroup: 64);
        var stages = lengths.BeginInputs().Then(write).Then(lengths.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, referenceStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        for (int index = 0; index < actual.Length; index++)
        {
            Assert.InRange(Vector3.Distance(reference.Read<BodyPose>(expected[index]).Position, world.Read<BodyPose>(actual[index]).Position), 0, 3e-5f);
            Assert.InRange(Vector3.Distance(reference.Read<BodyVelocity>(expected[index]).Linear, world.Read<BodyVelocity>(actual[index]).Linear), 0, 3e-4f);
        }
        Near(Vector3.Zero, world.Read<BodyPose>(actual[0]).Position);
        Near(new(7, 0, 0), world.Read<BodyVelocity>(actual[0]).Linear);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
        reference.ReleaseQuery(referenceRows); reference.ReleaseQuery(referenceBodies);
    }

    private DeviceGraphKernels CompileLengthGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_length_graph", """
            #include "physics_length_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceLengthBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceXpbdKernels CompileGraphLengthProjection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_length_colors", """
            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_length.slang"
            #include "physics_length_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceLengthBodies, DeviceXpbdLengthProjection<DeviceGraphBodies>> rows;
                initializeDeviceXpbdGraph(group.x, thread.x, rows);
            }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceLengthBodies, DeviceXpbdLengthProjection<DeviceGraphBodies>> rows;
                projectDeviceXpbdGraph(group.x, thread.x, rows);
            }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private struct SelectedXpbdBody : IComponent;
}
