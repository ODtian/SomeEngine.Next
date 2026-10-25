using System.Numerics;
using SomeEngine.Physics.Research;
using Xunit;

namespace SomeEngine.Physics.Tests;

public sealed class AvbdBehaviorTests
{
    [Fact]
    public void AngularLocksPreserveTheirInitialRelativePoseInFreeSpace()
    {
        using var world = new PhysicsDemo(PhysicsScene.CounterweightedJoint) { Gravity = 0, SleepingEnabled = false };
        PhysicsBody[] initial = world.ReadBodies();
        for (int frame = 0; frame < 60; ++frame) world.Step();
        PhysicsBody[] current = world.ReadBodies();
        for (int body = 0; body < current.Length; ++body)
        {
            Assert.InRange(Vector3.Distance(initial[body].Position, current[body].Position), 0, 0.0001f);
            Assert.InRange(Math.Abs(Quaternion.Dot(initial[body].Rotation, current[body].Rotation)), 0.999999f, 1.000001f);
        }
    }

    [Fact]
    public void FreeCollisionsKeepPenetrationBelowOneMillimetreWithTwoIterations()
    {
        using var world = new PhysicsDemo(PhysicsScene.FreeCollisions, 2) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.001);
    }

    [Fact]
    public void MassColumnsKeepCollisionPenetrationBelowOneMillimetreWithOneSubstepAndFiveIterations()
    {
        using var world = new PhysicsDemo(PhysicsScene.MassColumns, 5) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.001);
    }

    [Fact]
    public void YawedColumnsKeepCollisionPenetrationBelowOneMillimetreWithOneSubstepAndSixIterations()
    {
        using var world = new PhysicsDemo(PhysicsScene.YawedColumns, 6) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.001);
    }

    [Fact]
    public void TheRotatingRampTrajectoryKeepsGeometricPenetrationBelowTwoMillimetres()
    {
        using var world = new PhysicsDemo(PhysicsScene.RotatingRamp, 8, PhysicsSolver.Avbd, PhysicsMaterialModel.CompliantMaterials)
        { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 180; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.002);
    }

    [Fact]
    public void TheFirstRotatingRampImpactKeepsGeometricPenetrationBelowOneMillimetre()
    {
        using var world = new PhysicsDemo(PhysicsScene.RotatingRamp, 8, PhysicsSolver.Avbd, PhysicsMaterialModel.CompliantMaterials) { SleepingEnabled = false };
        Assert.Equal(1, world.Substeps);
        for (int frame = 0; frame < 16; ++frame) world.Step();
        Assert.InRange(world.ReadPenetration(), 0, 0.001);
    }

    [Fact]
    public void LongRangeConstraintsKeepTheSameFreeFlightAsAvbd()
    {
        using var avbd = new PhysicsDemo(PhysicsScene.ObliqueCollisions, 2, PhysicsSolver.Avbd) { SleepingEnabled = false };
        using var lrc = new PhysicsDemo(PhysicsScene.ObliqueCollisions) { SleepingEnabled = false };
        for (int frame = 0; frame < 20; ++frame) { avbd.Step(); lrc.Step(); }
        PhysicsBody[] expected = avbd.ReadBodies(), actual = lrc.ReadBodies();
        for (int body = 0; body < actual.Length; ++body)
        {
            Assert.InRange(Vector3.Distance(expected[body].Position, actual[body].Position), 0, 0.000001);
            Assert.InRange(Vector3.Distance(expected[body].Velocity, actual[body].Velocity), 0, 0.000001);
        }
    }

    [Fact]
    public void FourExplicitSubstepsKeepPenetrationBelowFiveMillimetresAfterASideImpulse()
    {
        using var world = new PhysicsDemo(2, 11, materialModel: PhysicsMaterialModel.CompliantMaterials) { SleepingEnabled = false, Substeps = 4 };
        double penetration = 0;
        for (int frame = 0; frame < 180; ++frame)
        {
            if (frame == 90) world.AddVelocity(world.BodyCount - 1, new(2, 0, 0));
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.005);
    }

    [Fact]
    public void RigidStackKeepsPenetrationBelowOneMillimetreAfterASideImpulseAtTheDefaultBudget()
    {
        using var world = new PhysicsDemo(2, 11) { SleepingEnabled = false };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            if (frame == 90) world.AddVelocity(world.BodyCount - 1, new(2, 0, 0));
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.001);
    }

    [Theory]
    [InlineData(7, 0.004)]
    [InlineData(8, 0.003)]
    public void RigidBranchedJointsKeepCollisionPenetrationWithinTheIterationBudget(int iterations, double tolerance)
    {
        using var world = new PhysicsDemo(3, 103, iterations) { SleepingEnabled = false };
        Assert.Equal(PhysicsMaterialModel.RigidConstraints, world.MaterialModel);
        Assert.Equal(1, world.Substeps);
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            if (frame == 90) world.AddVelocity(world.BodyCount - 1, new(0, 2, 0));
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, tolerance);
    }

    [Fact]
    public void RigidRopeKeepsCollisionPenetrationBelowTwoMillimetresWithEightIterations()
    {
        using var world = new PhysicsDemo(3, 6, 8) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.002);
    }

    [Fact]
    public void RigidRopeKeepsCollisionPenetrationBelowTwoMillimetresAfterASpatialImpulse()
    {
        using var world = new PhysicsDemo(3, 6, 8) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            if (frame == 90) world.AddVelocity(world.BodyCount - 1, new(0, 2, 0));
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.002);
    }

    [Fact]
    public void RigidSpatialStackKeepsCollisionPenetrationBelowOneAndAHalfMillimetresWithSevenIterations()
    {
        using var world = new PhysicsDemo(3, 9, 7) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            if (frame == 90) world.AddVelocity(world.BodyCount - 1, new(0, 2, 0));
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.0015);
    }

    [Fact]
    public void IslandImpactKeepsCollisionPenetrationBelowFiveMillimetresWithThreeIterations()
    {
        using var world = new PhysicsDemo(PhysicsScene.IslandImpact, 3) { SleepingEnabled = false, Substeps = 1 };
        double penetration = 0;
        for (int frame = 0; frame < 360; ++frame)
        {
            world.Step();
            penetration = Math.Max(penetration, world.ReadPenetration());
        }
        Assert.InRange(penetration, 0, 0.005);
    }
}
