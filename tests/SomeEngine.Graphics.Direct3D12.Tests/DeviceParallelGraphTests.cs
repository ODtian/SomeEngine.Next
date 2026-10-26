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
    public void ParallelDeviceGraphSolvesItsConnectedChainIndependentIslandsAndFreeBodies()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using World reference = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 269);
        using var lengths = new DeviceConstraintData<DeviceLengthConstraint>(backend, device, 258, "DeviceLengths");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 258, "DeviceLengthMultipliers");
        var actual = new Entity[269]; var expected = new Entity[269];
        for (int index = 0; index < actual.Length; index++)
        {
            Vector3 position = new(index < 257 ? index * 1.2f : index < 259 ? 400 + (index - 257) * 1.2f : index < 261 ? 500 + (index - 259) * 1.2f : 600 + (index - 261) * 3, 0, 0);
            actual[index] = AddBody(world, bodies, index, position, BodyMotion.Dynamic);
            if (index != 0) world.Add(actual[index], new SelectedXpbdBody());
            expected[index] = reference.CreateEntity(new BodyPose { Position = position, Rotation = Quaternion.Identity });
            reference.Add(expected[index], new BodyMass { Motion = index == 0 ? BodyMotion.Static : BodyMotion.Dynamic, InverseMass = 1 });
            reference.Add(expected[index], new BodyVelocity()); reference.Add(expected[index], new BodyStepState());
        }
        world.Replace(actual[0], new BodyVelocity { Linear = new(7, 0, 0) });
        reference.Replace(expected[0], new BodyVelocity { Linear = new(7, 0, 0) });
        for (int index = 0; index < 258; index++)
        {
            int first = index < 256 ? index : index == 256 ? 257 : 259;
            var definition = new XpbdLength { RestLength = 1, Compliance = .01f };
            RelationEdge<XpbdLength> edge = world.CreateRelation(actual[first], actual[first + 1], definition);
            reference.CreateRelation(expected[first], expected[first + 1], definition);
            world.Add(edge.Entity, new DeviceConstraintBinding<DeviceLengthConstraint>(lengths, index));
            world.Add(edge.Entity, new DeviceXpbdState<float>(multipliers));
        }
        world.Replace(actual[268], new BodyVelocity { Linear = Vector3.UnitY });
        reference.Replace(expected[268], new BodyVelocity { Linear = Vector3.UnitY });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>().All<SelectedXpbdBody>());
        QueryHandle rows = world.Query(world.QueryDefinition().Read<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>()
            .Read<DeviceConstraintBinding<DeviceLengthConstraint>>().All<DeviceXpbdState<float>>());
        QueryHandle referenceBodies = reference.Query(reference.QueryDefinition().ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle referenceRows = reference.Query(reference.QueryDefinition().ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        using var scalar = SolveXpbdGraph.Create(reference, referenceBodies,
            new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(referenceRows),
            new(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 2, BodiesPerBatch: 4, LargeIslandBodyLimit: 1, RowsPerBlock: 2, UseSimd: false), 64);
        var referenceStages = new PredictBodies(Vector3.Zero, 64).Then(scalar).Then(new UpdateBodyVelocities(64));
        using var write = lengths.WriteInputs<XpbdLength, DeviceLengthSource>(world, rows, new(), 64);
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var selection = new DeviceConstraintSelection<XpbdLength, DeviceLengthConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, rows, lengths, new(multipliers), "DeviceLengthsSelection");
        var bindings = new Dictionary<string, ResourceBinding[]>();
        lengths.AddBindings(bindings); multipliers.AddBindings(bindings);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileParallelLengthGraph(),
            new(258, 2), bindings, lengths.GetReadAccesses(jobs), 64, new[] { selection });
        using var solve = new DeviceXpbdSolver(backend, device, bodies, graph, CompileGraphLengthProjection(),
            new DeviceXpbdSettings(Iterations: 64, RowsPerBlock: 2), bindings, lengths.GetReadAccesses(jobs),
            new[] { multipliers.States }, threadsPerGroup: 64);
        var stages = lengths.BeginInputs().Then(write).Then(lengths.UploadInputs())
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(reference, QueryDefinition.Empty, referenceStages, 1).Schedule(0, 0, .1).Complete();
        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        for (int index = 0; index < actual.Length; index++)
        {
            Assert.InRange(Vector3.Distance(reference.Read<BodyPose>(expected[index]).Position, world.Read<BodyPose>(actual[index]).Position), 0, 8e-5f);
            Assert.InRange(Vector3.Distance(reference.Read<BodyVelocity>(expected[index]).Linear, world.Read<BodyVelocity>(actual[index]).Linear), 0, 8e-4f);
        }
        Near(Vector3.Zero, world.Read<BodyPose>(actual[0]).Position);
        Near(new(7, 0, 0), world.Read<BodyVelocity>(actual[0]).Linear);
        world.ReleaseQuery(rows); world.ReleaseQuery(members);
        reference.ReleaseQuery(referenceRows); reference.ReleaseQuery(referenceBodies);
    }

    private DeviceGraphKernels CompileParallelLengthGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "parallel_length_graph", """
        #include "physics_length_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceLengthBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());
}