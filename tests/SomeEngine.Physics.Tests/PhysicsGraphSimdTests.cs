using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsGraphSimdTests
{
    [Fact]
    public void VectorLengthBatchesMatchTheColoredScalarOrderAndPreserveTheLengthResponse()
    {
        using var scalarJobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var colorJobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var vectorJobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World scalar = WorldJobs.CreateWorld(scheduler: scalarJobs);
        using World color = WorldJobs.CreateWorld(scheduler: colorJobs);
        using World vector = WorldJobs.CreateWorld(scheduler: vectorJobs);
        int count = 2 * (Vector<float>.Count + 3) + 1;
        Entity[] scalarBodies = CreateChain(scalar, count);
        Entity[] colorBodies = CreateChain(color, count);
        Entity[] vectorBodies = CreateChain(vector, count);
        QueryHandle scalarBodyQuery = scalar.Query(new QueryDefinitionBuilder()
            .ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle vectorBodyQuery = vector.Query(new QueryDefinitionBuilder()
            .ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle colorBodyQuery = color.Query(new QueryDefinitionBuilder()
            .ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
        QueryHandle scalarLengths = scalar.Query(new QueryDefinitionBuilder()
            .ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle vectorLengths = vector.Query(new QueryDefinitionBuilder()
            .ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        QueryHandle colorLengths = color.Query(new QueryDefinitionBuilder()
            .ReadWrite<XpbdLength>().Read<UndirectedRelationEndpoints<XpbdLength>>());
        var acceleration = new Vector3(0, -2, 1);
        var scalarStages = new PredictBodies(acceleration, 16)
            .Then(SolveXpbd.Create(scalar, scalarBodyQuery,
                new XpbdConstraints<XpbdLength, XpbdLengthProjection>(scalarLengths), iterations: 32))
            .Then(new UpdateBodyVelocities(16));
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 0,
            BodiesPerBatch: 128, LargeIslandBodyLimit: 1, RowsPerBlock: 2 * Vector<float>.Count + 3, UseSimd: false);
        using var colorSolve = SolveXpbdGraph.Create(color, colorBodyQuery,
            new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(colorLengths), settings, iterations: 32);
        using var colored = SolveXpbdGraph.Create(vector, vectorBodyQuery,
            new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(vectorLengths), settings with { UseSimd = true }, iterations: 32);
        var colorStages = new PredictBodies(acceleration, 16).Then(colorSolve).Then(new UpdateBodyVelocities(16));
        var vectorStages = new PredictBodies(acceleration, 16).Then(colored).Then(new UpdateBodyVelocities(16));

        PhysicsSimulation.Create(scalar, QueryDefinition.Empty, scalarStages, substeps: 2).Schedule(3, .5, .25).Complete();
        PhysicsSimulation.Create(color, QueryDefinition.Empty, colorStages, substeps: 2).Schedule(3, .5, .25).Complete();
        PhysicsSimulation.Create(vector, QueryDefinition.Empty, vectorStages, substeps: 2).Schedule(3, .5, .25).Complete();

        for (int index = 0; index < count; index++)
        {
            BodyPose actualPose = vector.Read<BodyPose>(vectorBodies[index]);
            BodyVelocity actualVelocity = vector.Read<BodyVelocity>(vectorBodies[index]);
            BodyPose expectedPose = color.Read<BodyPose>(colorBodies[index]);
            BodyVelocity expectedVelocity = color.Read<BodyVelocity>(colorBodies[index]);
            Assert.InRange(Vector3.Distance(expectedPose.Position, actualPose.Position), 0, 1e-5f);
            Assert.Equal(expectedPose.Rotation, actualPose.Rotation);
            Assert.InRange(Vector3.Distance(expectedVelocity.Linear, actualVelocity.Linear), 0, 1e-4f);
            Assert.Equal(expectedVelocity.Angular, actualVelocity.Angular);
        }
        Assert.Equal(Vector3.Zero, scalar.Read<BodyPose>(scalarBodies[0]).Position);
        Assert.Equal(Vector3.Zero, vector.Read<BodyPose>(vectorBodies[0]).Position);
        Assert.InRange(scalar.Read<BodyPose>(scalarBodies[1]).Position.Length(), 1.1f, 1.313f);
        Assert.InRange(vector.Read<BodyPose>(vectorBodies[1]).Position.Length(), 1.1f, 1.313f);
        Assert.True(scalar.Read<BodyVelocity>(scalarBodies[1]).Linear.X < 0);
        Assert.True(vector.Read<BodyVelocity>(vectorBodies[1]).Linear.X < 0);
        scalar.ReleaseQuery(scalarLengths);
        vector.ReleaseQuery(vectorLengths);
        color.ReleaseQuery(colorLengths);
        scalar.ReleaseQuery(scalarBodyQuery);
        vector.ReleaseQuery(vectorBodyQuery);
        color.ReleaseQuery(colorBodyQuery);
    }

    private static Entity[] CreateChain(World world, int count)
    {
        var bodies = new Entity[count];
        for (int index = 0; index < count; index++)
        {
            Entity body = world.CreateEntity(new BodyPose
            {
                Position = new(index * 1.3f, .1f * (index % 3), .15f * (index % 5)),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .03f * index),
            });
            world.Add(body, new BodyMass
            {
                Motion = index == 0 ? BodyMotion.Static : BodyMotion.Dynamic,
                InverseMass = index == 0 ? 0 : 1 + .25f * (index % 4),
                CenterOfMass = new(.1f, .2f, .05f),
                InverseInertia = new Matrix4x4(2, .1f, 0, 0, .1f, 3, .2f, 0, 0, .2f, 4, 0, 0, 0, 0, 1),
            });
            world.Add(body, new BodyVelocity { Linear = new(.001f * index, 0, 0), Angular = new(0, .01f, 0) });
            world.Add(body, new BodyStepState());
            bodies[index] = body;
        }
        for (int index = 0; index + 1 < count; index++)
            world.CreateRelation(bodies[index], bodies[index + 1], new XpbdLength
            { RestLength = 1.1f + .03f * (index % 3), Compliance = .2f, Multiplier = .75f });
        return bodies;
    }
}
