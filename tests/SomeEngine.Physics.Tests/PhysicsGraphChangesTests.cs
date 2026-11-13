using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsGraphChangesTests
{
    [Fact]
    public void IndependentDynamicBodiesKeepTheirMotionWhenSmallIslandBatchesChange()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        Entity first = CreateBody(world, Vector3.Zero, new(1, 0, 0));
        Entity second = CreateBody(world, new(10, 0, 0), new(2, 0, 0));
        Entity third = CreateBody(world, new(20, 0, 0), new(-1, 0, 0));
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>()
            .WithStored<BodyStepState>().WithStored<BodyVelocity>().WithStored<AvbdBodyEquation>());
        Query joints = world.Query(new QueryDescription().WithStored<JointConstraint>()
            .WithStored<RelationEndpoints<JointConstraint>>().WithStored<AvbdJointState>());
        using var solve = SolveAvbdGraph.Create(world, bodies,
            new AvbdConstraints<JointConstraint, AvbdJointMath>(joints),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, SmallIslandBodyLimit: 2,
                BodiesPerBatch: 2, LargeIslandBodyLimit: 4, RowsPerBlock: 2, UseSimd: false),
            iterations: 1, multiplierRetention: 0, initialErrorFraction: 0);
        var stages = new PredictBodies(new Vector3(0, -1, 0), 2).Then(solve).Then(new UpdateBodyVelocities(2));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);

        physics.Schedule(0, 0, .5).Complete();

        Assert.Equal(new Vector3(.5f, -.25f, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(11, -.25f, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(19.5f, -.25f, 0), world.ReadComponentData<BodyPose>(third).Position);
        Entity added = CreateBody(world, new(30, 0, 0), new(3, 0, 0));
        BodyMass mass = world.ReadComponentData<BodyMass>(first);
        mass.Motion = BodyMotion.Static;
        world.Replace(first, mass);

        physics.Schedule(1, .5, .5).Complete();

        Assert.Equal(new Vector3(.5f, -.25f, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(12, -.75f, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(19, -.75f, 0), world.ReadComponentData<BodyPose>(third).Position);
        Assert.Equal(new Vector3(31.5f, -.25f, 0), world.ReadComponentData<BodyPose>(added).Position);
        Assert.Equal(new Vector3(2, -1, 0), world.ReadComponentData<BodyVelocity>(second).Linear);
        Assert.Equal(new Vector3(-1, -1, 0), world.ReadComponentData<BodyVelocity>(third).Linear);
        Assert.Equal(new Vector3(3, -.5f, 0), world.ReadComponentData<BodyVelocity>(added).Linear);
    }

    [Fact]
    public void ABodyChangingBetweenDynamicAndStaticSplitsAndJoinsItsLengthConstraints()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        Entity first = CreateBody(world, Vector3.Zero, Vector3.Zero);
        Entity middle = CreateBody(world, new(4, 0, 0), Vector3.Zero);
        Entity last = CreateBody(world, new(8, 0, 0), Vector3.Zero);
        Relations<XpbdLength>.CreateEdge(world, first, middle, new XpbdLength { RestLength = 2 });
        Relations<XpbdLength>.CreateEdge(world, middle, last, new XpbdLength { RestLength = 2 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, SmallIslandBodyLimit: 1,
                BodiesPerBatch: 4, LargeIslandBodyLimit: 4, RowsPerBlock: 2, UseSimd: false), iterations: 1);
        var stages = new PredictBodies(Vector3.Zero, 4).Then(solve).Then(new UpdateBodyVelocities(4));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);
        physics.Schedule(0, 0, 1).Complete();
        BodyMass mass = world.ReadComponentData<BodyMass>(middle);
        mass.Motion = BodyMotion.Static;
        world.Replace(middle, mass);

        physics.Schedule(1, 1, 1).Complete();

        Assert.Equal(new Vector3(2.5f, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(4.5f, 0, 0), world.ReadComponentData<BodyPose>(middle).Position);
        Assert.Equal(new Vector3(6.5f, 0, 0), world.ReadComponentData<BodyPose>(last).Position);
        Assert.Equal(new Vector3(.5f, 0, 0), world.ReadComponentData<BodyVelocity>(middle).Linear);
        mass.Motion = BodyMotion.Dynamic;
        world.Replace(middle, mass);

        physics.Schedule(2, 2, 1).Complete();

        Assert.Equal(new Vector3(3.5f, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(5, 0, 0), world.ReadComponentData<BodyPose>(middle).Position);
        Assert.Equal(new Vector3(7, 0, 0), world.ReadComponentData<BodyPose>(last).Position);
        Assert.Equal(new Vector3(.5f, 0, 0), world.ReadComponentData<BodyVelocity>(middle).Linear);
    }

    [Fact]
    public void RetargetedAndRemovedLengthsRepairMergedAndSeparatedBodyMotion()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(jobs));
        Entity first = CreateBody(world, Vector3.Zero, Vector3.Zero);
        Entity second = CreateBody(world, new(4, 0, 0), Vector3.Zero);
        Entity third = CreateBody(world, new(10, 0, 0), Vector3.Zero);
        Entity free = CreateBody(world, new(50, 0, 0), new(2, 0, 0));
        RelationEdge<XpbdLength> connection = Relations<XpbdLength>.CreateEdge(world, first, second, new XpbdLength { RestLength = 2 });
        Query bodies = world.Query(new QueryDescription().WithStored<BodyPose>().WithStored<BodyMass>().WithStored<BodyStepState>());
        Query lengths = world.Query(new QueryDescription().WithStored<XpbdLength>().WithStored<RelationEndpoints<XpbdLength>>());
        using var solve = SolveXpbdGraph.Create(world, bodies,
            new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths),
            new PhysicsGraphSettings(PhysicsGraphOrder.Islands, SmallIslandBodyLimit: 1,
                BodiesPerBatch: 4, LargeIslandBodyLimit: 4, RowsPerBlock: 2, UseSimd: false), iterations: 1);
        var stages = new PredictBodies(Vector3.Zero, 4).Then(solve).Then(new UpdateBodyVelocities(4));
        var physics = PhysicsSimulation.Create(world, world.Query(new QueryDescription().With<BodyPose>().With<BodyMass>()), stages, 1);

        physics.Schedule(0, 0, 1).Complete();
        Assert.Equal(new Vector3(1, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(3, 0, 0), world.ReadComponentData<BodyPose>(second).Position);
        world.RetargetRelationImmediate(connection, second, third);

        physics.Schedule(1, 1, 1).Complete();
        Assert.Equal(new Vector3(2, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(5, 0, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(7, 0, 0), world.ReadComponentData<BodyPose>(third).Position);
        Relations<XpbdLength>.CreateEdge(world, first, second, new XpbdLength { RestLength = 2 });

        physics.Schedule(2, 2, 1).Complete();
        Assert.Equal(new Vector3(3.75f, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(5.75f, 0, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(4.5f, 0, 0), world.ReadComponentData<BodyPose>(third).Position);
        world.DestroyRelation(connection);

        physics.Schedule(3, 3, 1).Complete();
        Assert.Equal(new Vector3(5, 0, 0), world.ReadComponentData<BodyPose>(first).Position);
        Assert.Equal(new Vector3(7, 0, 0), world.ReadComponentData<BodyPose>(second).Position);
        Assert.Equal(new Vector3(2, 0, 0), world.ReadComponentData<BodyPose>(third).Position);
        Assert.Equal(new Vector3(1.25f, 0, 0), world.ReadComponentData<BodyVelocity>(first).Linear);
        Assert.Equal(new Vector3(1.25f, 0, 0), world.ReadComponentData<BodyVelocity>(second).Linear);
        Assert.Equal(new Vector3(-2.5f, 0, 0), world.ReadComponentData<BodyVelocity>(third).Linear);
        Assert.Equal(new Vector3(58, 0, 0), world.ReadComponentData<BodyPose>(free).Position);
        Assert.Equal(new Vector3(2, 0, 0), world.ReadComponentData<BodyVelocity>(free).Linear);
    }

    private static Entity CreateBody(World world, Vector3 position, Vector3 velocity)
    {
        Entity body = world.Create(new BodyPose { Position = position, Rotation = Quaternion.Identity });
        world.Add(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        world.Add(body, new BodyVelocity { Linear = velocity });
        world.Add(body, new BodyStepState());
        world.Add(body, new AvbdBodyEquation());
        return body;
    }
}
