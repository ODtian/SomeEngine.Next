using System.Numerics;
using SomeEngine.Liquid;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidNaturalFlowTests
{
    [Fact]
    public void ContinuousInletMovesRetainsRestartsAndDriesOnOneFixedSurface()
    {
        var surface = new LiquidSurface(new(.5f, -.02f, .0005f, 0), new(60, 0, 0, 0), new(0, .08f, .08f, .006f));
        var liquid = new LiquidFlow(scene: "curved", emission: LiquidEmission.Drops)
        {
            Gravity = new(0, -9.81f, 0),
            Wetting = new(5, 15, .00002f),
            Source = new(new(-.043f, surface.Height(-.043f, 0, out _), 0), Vector3.Zero, .02e-6, Duration: 6, SurfaceRadius: .002f),
            Surface = surface,
        };
        liquid.Material = liquid.Material with { Viscosity = .02f };
        liquid.Place(new(-.032f, surface.Height(-.032f, 0, out _), 0), .02e-6, new(.006f));
        var trace = new List<object>();
        double balance = 0;
        double? merged = null, retentionStart = null, retentionEnd = null, lineRestart = null, translationRestart = null;
        double retainedVolume = 0, volumeGrowth = 0, retainedDuration = 0;
        bool separateVolumes = false;
        for (int step = 0; step < 120 * 120; step++)
        {
            liquid.Step();
            balance = Math.Max(balance, Math.Abs(liquid.Volume + liquid.AbsorbedVolume + liquid.EvaporatedVolume - liquid.InjectedVolume));
            int attached = 0;
            foreach (ref readonly LiquidPoint point in liquid.Points) if (point.Surface != LiquidContactSurface.None) attached++;
            if (attached > 1) separateVolumes = true;
            if (separateVolumes && attached == 1) merged ??= liquid.Seconds;
            if (merged.HasValue && liquid.Seconds < liquid.Source.Duration && liquid.Count > 0)
            {
                LiquidPoint resident = liquid.Points[0];
                if (!lineRestart.HasValue && resident.Motion == LiquidMotion.Resting)
                {
                    if (!retentionStart.HasValue) { retentionStart = liquid.Seconds; retainedVolume = resident.Volume; }
                    retentionEnd = liquid.Seconds;
                    retainedDuration = retentionEnd.Value - retentionStart.Value;
                    volumeGrowth = resident.Volume - retainedVolume;
                }
                else if (!lineRestart.HasValue)
                {
                    if (retainedDuration >= .25 && volumeGrowth > 0) lineRestart = liquid.Seconds;
                    else { retentionStart = retentionEnd = null; retainedDuration = volumeGrowth = 0; }
                }
                if (lineRestart.HasValue && resident.Velocity.Length() > .00001f) translationRestart ??= liquid.Seconds;
            }
            if (step % 6 == 0 || liquid.Count == 0 && liquid.Seconds > liquid.Source.Duration)
            {
                var points = new List<object>();
                Vector3 centre = Vector3.Zero;
                foreach (ref readonly LiquidPoint point in liquid.Points)
                {
                    centre += point.Position * (float)point.Volume;
                    points.Add(new { point.Number, X = point.Position.X, Y = point.Position.Y, Z = point.Position.Z,
                        AxisX = point.Axis.X, AxisY = point.Axis.Y, AxisZ = point.Axis.Z,
                        NormalX = point.Normal.X, NormalY = point.Normal.Y, NormalZ = point.Normal.Z,
                        Speed = point.Velocity.Length(), point.Volume, point.Radius, point.Width,
                        point.RadialVelocity, point.WidthVelocity, Motion = point.Motion.ToString(), Surface = point.Surface.ToString() });
                }
                trace.Add(new { liquid.Seconds, liquid.Volume, liquid.InjectedVolume, liquid.AbsorbedVolume,
                    X = liquid.Volume > 0 ? centre.X / liquid.Volume : (double?)null, Count = liquid.Count, Points = points });
            }
            if (liquid.Count == 0 && liquid.Seconds > liquid.Source.Duration) break;
        }
        LiquidReferenceEvidence.Write("natural-flow-points", new { Gravity = new[] { liquid.Gravity.X, liquid.Gravity.Y, liquid.Gravity.Z },
            Surface = "0.5 - 0.02*x + 0.0005*sin(60*x)", InletRate = liquid.Source.VolumeRate,
            InletDuration = liquid.Source.Duration, AbsorptionSpeed = liquid.Wetting.AbsorptionSpeed,
            liquid.Material.Density, liquid.Material.Viscosity, liquid.Material.SurfaceTension,
            liquid.Wetting.RecedingDegrees, liquid.Wetting.AdvancingDegrees, liquid.Wetting.RetentionFactor,
            InletX = liquid.Source.Position.X, InletRadius = liquid.Source.SurfaceRadius, StepSeconds = LiquidFlow.StepSeconds,
            DisappearanceSeconds = liquid.Count == 0 ? liquid.Seconds : (double?)null,
            InitialVolume = .02e-6, InitialRadius = .006, InitialX = -.032,
            CoalescenceSeconds = merged, RetentionStartSeconds = retentionStart, RetentionEndSeconds = retentionEnd,
            RetentionDurationSeconds = retainedDuration, RetentionVolumeIncrease = volumeGrowth,
            ContactLineRestartSeconds = lineRestart, TranslationRestartSeconds = translationRestart,
            MaximumMassBalanceError = balance, Trace = trace });
        Assert.InRange(balance, 0, 1e-18);
        Assert.NotNull(merged);
        Assert.InRange(retainedDuration, .25, liquid.Source.Duration);
        Assert.True(volumeGrowth > 0);
        Assert.NotNull(translationRestart);
        Assert.InRange(translationRestart!.Value, 0, liquid.Source.Duration);
        Assert.Equal(0, liquid.Count);
    }
}
