using System.Numerics;
using SomeEngine.Liquid;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.8f)]
    public void EachReconstructedCurveContainsItsControlPointVolume(float disturbance)
    {
        LiquidPoint[] points = CreateStrand();
        points[1].Disturbance = disturbance;
        var curves = new LiquidCurve[points.Length];
        LiquidGeometry.BuildCurves(points, new(-1, Vector3.Zero, Vector3.Zero), curves);
        for (int index = 0; index < points.Length; index++)
        {
            double volume = 0;
            for (int sample = 0; sample <= 128; sample++)
            {
                float t = sample / 128f;
                volume += Math.PI * curves[index].RadiusSquared(t) * curves[index].Derivative(t).Length() *
                    (sample is 0 or 128 ? 1 : sample % 2 == 0 ? 2 : 4) / 384;
            }
            Assert.InRange(volume / points[index].Volume, .999, 1.001);
        }
    }

    [Fact]
    public void AdjacentLiquidCellsShareTheirBoundaryPositionAndRadius()
    {
        LiquidPoint[] points = CreateStrand();
        var curves = new LiquidCurve[points.Length];
        LiquidGeometry.BuildCurves(points, new(-1, Vector3.Zero, Vector3.Zero), curves);
        for (int index = 1; index < points.Length; index++)
        {
            Assert.Equal(curves[index - 1].End, curves[index].Start);
            Assert.Equal(curves[index - 1].RadiusSquared(1), curves[index].RadiusSquared(0));
        }
    }

    [Fact]
    public void FillingAnInletCellKeepsThePrescribedJetArea()
    {
        var liquid = new LiquidFlow(scene: "floor") { Gravity = Vector3.Zero };
        liquid.Source = new(new(0, 10, 0), Vector3.UnitX, 2e-6, Duration: .5f);
        liquid.Material = liquid.Material with { Viscosity = 0, SurfaceTension = 0 };
        for (int step = 0; step < 13; step++) liquid.Step();
        var curves = new LiquidCurve[liquid.Count];
        LiquidGeometry.BuildCurves(liquid.Points, liquid.Inlet, curves);
        LiquidCurve inlet = curves[liquid.Inlet.Point];
        double area = liquid.Points[liquid.Inlet.Point].Volume / inlet.Length;
        Assert.InRange(area / (liquid.Source.VolumeRate / liquid.Source.Velocity.Length()), .999, 1.001);
        Assert.Equal(liquid.Inlet.Front, curves[liquid.Points[liquid.Inlet.Point].Previous].End);
    }

    private static LiquidPoint[] CreateStrand() =>
    [
        new() { Position = new(.2f, 1, 0), Velocity = new(2, -.2f, 0), Volume = 1e-7, Previous = -1 },
        new() { Position = new(.1f, 1.01f, 0), Velocity = new(2, -.2f, 0), Volume = 2e-7, Previous = 0 },
        new() { Position = new(0, 1.03f, 0), Velocity = new(2, -.2f, 0), Volume = 1e-7, Previous = 1 },
    ];
}
