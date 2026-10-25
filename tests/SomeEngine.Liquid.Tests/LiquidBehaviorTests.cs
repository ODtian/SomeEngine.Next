using System.Numerics;
using SomeEngine.Liquid;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidBehaviorTests
{
    [Fact]
    public void FreeVolumeFollowsTheBallisticSolution()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false };
        Vector3 position = new(0, 10, 0), velocity = new(1, 2, 0);
        liquid.Inject(position, velocity, 1e-6);
        for (int step = 0; step < 24; step++) liquid.Step();
        float seconds = 24 * LiquidFlow.StepSeconds;
        Vector3 expected = position + velocity * seconds + .5f * liquid.Gravity * seconds * seconds;
        Assert.InRange(Vector3.Distance(expected, liquid.Points[0].Position), 0, .00002f);
        Assert.InRange(Vector3.Distance(velocity + liquid.Gravity * seconds, liquid.Points[0].Velocity), 0, .00002f);
    }

    [Fact]
    public void InletVolumeAndMomentumMatchAcrossPointBudgets()
    {
        var small = new LiquidFlow(8, "floor") { Gravity = Vector3.Zero };
        var large = new LiquidFlow(64, "floor") { Gravity = Vector3.Zero };
        small.Source = large.Source = new(new(0, 10, 0), new(.1f, 0, 0), 2e-6, Duration: 2);
        small.Material = large.Material = small.Material with { Viscosity = 0, SurfaceTension = 0, Elasticity = 0 };
        for (int step = 0; step < 240; step++) { small.Step(); large.Step(); }
        Assert.InRange(Math.Abs(small.Volume - 4e-6), 0, 1e-13);
        Assert.InRange(Math.Abs(large.Volume - 4e-6), 0, 1e-13);
        Vector3 expected = new(.0004f, 0, 0);
        Assert.InRange(Vector3.Distance(expected, small.Momentum), 0, 1e-8f);
        Assert.InRange(Vector3.Distance(expected, large.Momentum), 0, 1e-8f);
        Assert.InRange(small.Count, 1, small.PointLimit);
    }

    [Fact]
    public void PulseVolumeMatchesTheIntegratedInletFlux()
    {
        var liquid = new LiquidFlow(scene: "floor", emission: LiquidEmission.Pulses) { Gravity = Vector3.Zero };
        liquid.Source = new(new(0, 10, 0), new(.1f, 0, 0), 2e-6, Duration: .72f);
        liquid.Material = liquid.Material with { Viscosity = 0, SurfaceTension = 0 };
        for (int step = 0; step < 88; step++) liquid.Step();
        double expected = 2 * liquid.Source.VolumeRate * liquid.Source.PulseSeconds / Math.PI;
        Assert.InRange(Math.Abs(liquid.Volume - expected), 0, 1e-13);
    }

    [Fact]
    public void ElasticStressDelaysCapillarySeparation()
    {
        var water = new LiquidFlow(scene: "floor") { Gravity = Vector3.Zero };
        var elastic = new LiquidFlow(scene: "floor") { Gravity = Vector3.Zero };
        water.Source = elastic.Source = new(new(0, 10, 0), new(1, 0, 0), 2e-6, Duration: .4f, SampleSeconds: .02f);
        elastic.Material = water.Material with { Elasticity = 30, RelaxationSeconds = .42f };
        for (int step = 0; step < 36; step++) { water.Step(); elastic.Step(); }
        int freeWater = water.Points.ToArray().Count(point => point.Previous < 0);
        int freeElastic = elastic.Points.ToArray().Count(point => point.Previous < 0);
        Assert.True(freeWater > freeElastic);
        Assert.InRange(Math.Abs(water.Volume - elastic.Volume), 0, 1e-13);
    }

    [Theory]
    [InlineData(LiquidFluid.Water)]
    [InlineData(LiquidFluid.Urine)]
    [InlineData(LiquidFluid.Semen)]
    public void LiquidRetainsItsVolumeAfterPouringStops(LiquidFluid fluid)
    {
        var liquid = new LiquidFlow(fluid: fluid);
        for (int step = 0; step < 120; step++) liquid.Step();
        liquid.Pouring = false;
        double before = liquid.Volume;
        for (int step = 0; step < 480; step++) liquid.Step();
        Assert.InRange(Math.Abs(before - liquid.Volume), 0, 1e-13);
    }

    [Fact]
    public void SurfaceCoalescenceConservesVolumeAndTangentialMomentum()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero };
        liquid.Material = liquid.Material with { Viscosity = 0, SurfaceTension = 0 };
        liquid.Inject(new(-.001f, LiquidFlow.TrayFloor, 0), new(.1f, 0, 0), 1e-6);
        liquid.Inject(new(.001f, LiquidFlow.TrayFloor, 0), new(.05f, 0, 0), 2e-6);
        Vector3 momentum = liquid.Momentum;
        liquid.Step();
        Assert.Single(liquid.Points.ToArray());
        Assert.InRange(Math.Abs(liquid.Volume - 3e-6), 0, 1e-14);
        Assert.InRange(Vector3.Distance(momentum, liquid.Momentum), 0, 1e-8f);
    }

    [Fact]
    public void SurfaceCoalescencePreservesTheSecondMomentOfItsLiquid()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero };
        liquid.Material = liquid.Material with { Viscosity = .02f, SurfaceTension = 0 };
        double volume = 1e-6;
        float radius = MathF.Cbrt((float)(volume * 3 / (4 * Math.PI))), separation = radius * .8f;
        liquid.Place(new(-separation, LiquidFlow.TrayFloor, 0), volume, new(radius));
        liquid.Place(new(separation, LiquidFlow.TrayFloor, 0), volume, new(radius));
        double initialArea = ReadArea(), initialCentreHeight = ReadHeight(0, 0);
        liquid.Step();
        double varianceX = 0, varianceZ = 0;
        foreach (ref readonly LiquidPoint point in liquid.Points)
        {
            Vector3 across = Vector3.Cross(point.Normal, point.Axis);
            double fraction = point.Volume / liquid.Volume;
            varianceX += fraction * (point.Position.X * point.Position.X +
                (point.Radius * point.Radius * point.Axis.X * point.Axis.X + point.Width * point.Width * across.X * across.X) / 6);
            varianceZ += fraction * (point.Position.Z * point.Position.Z +
                (point.Radius * point.Radius * point.Axis.Z * point.Axis.Z + point.Width * point.Width * across.Z * across.Z) / 6);
        }
        double area = ReadArea(), centreHeight = ReadHeight(0, 0);
        LiquidReferenceEvidence.Write("stationary-contact", new
        {
            Reference = "Zero gravity, zero surface tension and zero velocity: p is constant, q=0, h_t=0",
            InitialVolumePerPoint = volume, InitialRadius = radius, InitialCentreDistance = 2 * separation,
            liquid.Material.Viscosity, liquid.Material.SurfaceTension,
            InitialContactArea = initialArea, ContactArea = area,
            RelativeAreaError = Math.Abs(area / initialArea - 1),
            InitialCentreHeight = initialCentreHeight, CentreHeight = centreHeight,
            RelativeCentreHeightError = Math.Abs(centreHeight / initialCentreHeight - 1),
            liquid.Volume, AreaSamplesPerAxis = 128,
        });
        Assert.InRange(Math.Abs(varianceX - (radius * radius / 6 + separation * separation)), 0, 1e-10);
        Assert.InRange(Math.Abs(varianceZ - radius * radius / 6), 0, 1e-10);

        double ReadHeight(float x, float z)
        {
            double height = 0;
            foreach (ref readonly LiquidPoint point in liquid.Points)
            {
                Vector3 delta = new Vector3(x, LiquidFlow.TrayFloor + LiquidFlow.ContactRadius, z) - point.Position;
                double along = Vector3.Dot(delta, point.Axis) / point.Radius;
                double across = Vector3.Dot(delta, Vector3.Cross(point.Normal, point.Axis)) / point.Width;
                height += 2 * point.Volume / (Math.PI * point.Radius * point.Width) * Math.Max(0, 1 - along * along - across * across);
            }
            return height;
        }

        double ReadArea()
        {
            float spacing = 8 * radius / 128;
            int wetCells = 0;
            for (int x = 0; x < 128; x++)
            for (int z = 0; z < 128; z++)
                if (ReadHeight((x + .5f) * spacing - 4 * radius, (z + .5f) * spacing - 4 * radius) > 0) wetCells++;
            return wetCells * (double)spacing * spacing;
        }
    }

    [Fact]
    public void CoalescedLiquidRemainsAttachedToTheCurvedSurface()
    {
        var liquid = new LiquidFlow(scene: "curved") { Pouring = false, Gravity = Vector3.Zero };
        liquid.Material = liquid.Material with { Viscosity = 0, SurfaceTension = 0 };
        liquid.Inject(new(-.52f, liquid.Surface.Height(-.52f, 0, out _), 0), Vector3.Zero, 1e-4);
        liquid.Inject(new(-.48f, liquid.Surface.Height(-.48f, 0, out _), 0), Vector3.Zero, 1e-4);
        liquid.Step(); liquid.Step();
        LiquidPoint point = Assert.Single(liquid.Points.ToArray());
        Assert.Equal(LiquidContactSurface.CurvedTop, point.Surface);
    }

    [Fact]
    public void ContactLineRetentionHoldsSmallLiquidAtRest()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = new(.2f, -9.81f, 0) };
        liquid.Inject(new(0, LiquidFlow.TrayFloor, 0), new(.1f, 0, 0), 1e-9);
        for (int step = 0; step < 120; step++) liquid.Step();
        LiquidPoint resting = liquid.Points[0];
        Assert.Equal(LiquidMotion.Resting, resting.Motion);
        for (int step = 0; step < 120; step++) liquid.Step();
        Assert.Equal(resting.Position, liquid.Points[0].Position);
        Assert.Equal(Vector3.Zero, liquid.Points[0].Velocity);
    }

    [Fact]
    public void AdditionalVolumeCanOvercomeContactLineRetention()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = new(.5f, -9.81f, 0) };
        liquid.Inject(new(0, LiquidFlow.TrayFloor, 0), Vector3.Zero, 1e-9);
        for (int step = 0; step < 120; step++) liquid.Step();
        Assert.Equal(LiquidMotion.Resting, liquid.Points[0].Motion);
        liquid.Inject(liquid.Points[0].Position, Vector3.Zero, 1e-5);
        liquid.Step(); liquid.Step();
        Assert.True(liquid.Momentum.X > 0);
        Assert.Equal(LiquidMotion.Flowing, liquid.Points[0].Motion);
    }

    [Fact]
    public void SurfaceFlowApproachesTheViscousFilmVelocity()
    {
        var liquid = new LiquidFlow(scene: "floor")
        { Pouring = false, Gravity = new(.02f, -9.81f, 0), Wetting = new(45, 45) };
        liquid.Material = liquid.Material with { Viscosity = .05f };
        liquid.Inject(new(0, LiquidFlow.TrayFloor, 0), Vector3.Zero, 1e-8);
        for (int step = 0; step < 1200; step++) liquid.Step();
        LiquidPoint point = liquid.Points[0];
        float expected = liquid.Gravity.X * liquid.Material.Density * point.Height * point.Height / (3 * liquid.Material.Viscosity);
        Assert.InRange(point.Velocity.X / expected, .98f, 1.02f);
    }

    [Fact]
    public void AbsorbedVolumeTransfersToTheSurface()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Wetting = new(20, 70, .001f) };
        liquid.Inject(new(0, LiquidFlow.TrayFloor, 0), Vector3.Zero, 1e-12);
        for (int step = 0; step < 120; step++) liquid.Step();
        Assert.Empty(liquid.Points.ToArray());
        Assert.Equal(0, liquid.Volume);
        Assert.InRange(Math.Abs(liquid.AbsorbedVolume - liquid.InjectedVolume), 0, 1e-20);
    }

    [Fact]
    public void EvaporatedVolumeTransfersToTheVapor()
    {
        var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Evaporation = new(2.5e-5f, .00865f) };
        liquid.Inject(new(0, LiquidFlow.TrayFloor, 0), Vector3.Zero, 1e-15);
        for (int step = 0; step < 120; step++) liquid.Step();
        Assert.Empty(liquid.Points.ToArray());
        Assert.InRange(Math.Abs(liquid.EvaporatedVolume - liquid.InjectedVolume), 0, 1e-22);
    }

    [Fact]
    public void JetContactFollowsTheCurvedSolidThroughEveryStep()
    {
        var liquid = new LiquidFlow(scene: "curved");
        int contactSteps = 0;
        for (int step = 0; step < 720; step++)
        {
            liquid.Step();
            foreach (ref readonly LiquidPoint point in liquid.Points)
            {
                Assert.True(liquid.Distance(point.Position, out _) >= -.0001f);
                if (point.Surface == LiquidContactSurface.None) continue;
                contactSteps++;
                Assert.InRange(Math.Abs(Vector3.Dot(point.Velocity, point.Normal)), 0, .0001f);
            }
        }
        Assert.True(contactSteps > 100);
    }
}
