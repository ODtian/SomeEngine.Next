using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
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
        using World scalar = new World(execution: new WorldJobs(scalarJobs));
        using World color = new World(execution: new WorldJobs(colorJobs));
        using World vector = new World(execution: new WorldJobs(vectorJobs));
        int count = 2 * (Vector<float>.Count + 3) + 1;
        Entity[] scalarBodies = CreateChain(scalar, count);
        Entity[] colorBodies = CreateChain(color, count);
        Entity[] vectorBodies = CreateChain(vector, count);
        Query scalarBodyQuery = scalar.Query(new QueryDescription()
            .WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query vectorBodyQuery = vector.Query(new QueryDescription()
            .WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query colorBodyQuery = color.Query(new QueryDescription()
            .WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query scalarLengths = scalar.Query(new QueryDescription()
            .WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query vectorLengths = vector.Query(new QueryDescription()
            .WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        Query colorLengths = color.Query(new QueryDescription()
            .WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        var acceleration = new Vector3(0, -2, 1);
        var scalarStages = new PredictBodies(acceleration, 16)
            .Then(SolveXpbd.Create(scalar, scalarBodyQuery,
                new XpbdConstraints<XpbdLength, XpbdLengthProjection>(scalarLengths), iterations: 32))
            .Then(new UpdateBodyVelocities(16));
        var settings = new PhysicsGraphSettings(PhysicsGraphOrder.Colors, SmallIslandBodyLimit: 0,
            BodiesPerBatch: 128, LargeIslandBodyLimit: 1, RowsPerBlock: 2 * Vector<float>.Count + 3, UseSimd: false);
        using var colorSolve = SolveXpbdGraph.Create(color, colorBodyQuery,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(colorLengths), settings, iterations: 32);
        using var colored = SolveXpbdGraph.Create(vector, vectorBodyQuery,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(vectorLengths), settings with { UseSimd = true }, iterations: 32);
        var colorStages = new PredictBodies(acceleration, 16).Then(colorSolve).Then(new UpdateBodyVelocities(16));
        var vectorStages = new PredictBodies(acceleration, 16).Then(colored).Then(new UpdateBodyVelocities(16));

        PhysicsSimulation.Create(scalar, scalar.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), scalarStages, substeps: 2).Schedule(3, .5, .25).Complete();
        PhysicsSimulation.Create(color, color.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), colorStages, substeps: 2).Schedule(3, .5, .25).Complete();
        PhysicsSimulation.Create(vector, vector.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), vectorStages, substeps: 2).Schedule(3, .5, .25).Complete();

        for (int index = 0; index < count; index++)
        {
            BodyPose actualPose = vector.ReadComponentData<BodyPose>(vectorBodies[index]);
            BodyVelocity actualVelocity = vector.ReadComponentData<BodyVelocity>(vectorBodies[index]);
            BodyPose expectedPose = color.ReadComponentData<BodyPose>(colorBodies[index]);
            BodyVelocity expectedVelocity = color.ReadComponentData<BodyVelocity>(colorBodies[index]);
            Assert.InRange(Vector3.Distance(expectedPose.Position, actualPose.Position), 0, 1e-5f);
            Assert.Equal(expectedPose.Rotation, actualPose.Rotation);
            Assert.InRange(Vector3.Distance(expectedVelocity.Linear, actualVelocity.Linear), 0, 1e-4f);
            Assert.Equal(expectedVelocity.Angular, actualVelocity.Angular);
        }
        Assert.Equal(Vector3.Zero, scalar.ReadComponentData<BodyPose>(scalarBodies[0]).Position);
        Assert.Equal(Vector3.Zero, vector.ReadComponentData<BodyPose>(vectorBodies[0]).Position);
        Assert.InRange(scalar.ReadComponentData<BodyPose>(scalarBodies[1]).Position.Length(), 1.1f, 1.313f);
        Assert.InRange(vector.ReadComponentData<BodyPose>(vectorBodies[1]).Position.Length(), 1.1f, 1.313f);
        Assert.True(scalar.ReadComponentData<BodyVelocity>(scalarBodies[1]).Linear.X < 0);
        Assert.True(vector.ReadComponentData<BodyVelocity>(vectorBodies[1]).Linear.X < 0);
    }

    private static Entity[] CreateChain(World world, int count)
    {
        var bodies = new Entity[count];
        for (int index = 0; index < count; index++)
        {
            Entity body = world.Create(new BodyPose
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
            Relations<XpbdLength>.CreateEdge(world, bodies[index], bodies[index + 1], new XpbdLength
            { RestLength = 1.1f + .03f * (index % 3), Compliance = .2f, Multiplier = .75f });
        return bodies;
    }
}
