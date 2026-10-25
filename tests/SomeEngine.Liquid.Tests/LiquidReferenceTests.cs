using System.Numerics;
using SomeEngine.Liquid;

namespace SomeEngine.Liquid.Tests;

public sealed class LiquidReferenceTests
{
    [Fact]
    public void ImpactTimeAndRadialEnergyFollowTheInviscidCollisionSolution()
    {
        var results = new List<object>();
        double radiusError = 0, speedError = 0, positionError = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero };
            liquid.Material = liquid.Material with { Viscosity = 0, SurfaceTension = 0 };
            liquid.Inject(new(0, LiquidFlow.TrayFloor + LiquidFlow.ContactRadius + .00425f, 0), new(.05f, -.2f, 0), 1e-6);
            LiquidPoint start = liquid.Points[0];
            double hit = (start.Position.Y - LiquidFlow.TrayFloor - LiquidFlow.ContactRadius) / .2f;
            double radialSpeed = Math.Sqrt(3) * .2f;
            var trace = new List<object>();
            for (int step = 0; step < frequency / 5; step++)
            {
                liquid.Step(1f / frequency);
                LiquidPoint point = liquid.Points[0];
                double radius = start.Radius + radialSpeed * Math.Max(0, liquid.Seconds - hit);
                radiusError = Math.Max(radiusError, Math.Abs(point.Radius - radius));
                positionError = Math.Max(positionError, Math.Abs(point.Position.X - .05f * liquid.Seconds));
                if (liquid.Seconds >= hit) speedError = Math.Max(speedError, Math.Abs(point.RadialVelocity - radialSpeed));
                trace.Add(new { liquid.Seconds, Radius = point.Radius, ReferenceRadius = radius, point.RadialVelocity, ReferenceRadialSpeed = liquid.Seconds >= hit ? radialSpeed : 0 });
            }
            results.Add(new { Frequency = frequency, ImpactSeconds = hit, Trace = trace });
        }
        LiquidReferenceEvidence.Write("impact-time", new { Reference = "Ballistic intersection; rho*V*vn^2/2 = rho*V*rdot^2/6; r=r0+rdot*(t-t_hit)",
            MaximumRadiusErrorMetres = radiusError, MaximumRadialSpeedErrorMetresPerSecond = speedError, MaximumPositionErrorMetres = positionError, Results = results });
        Assert.InRange(radiusError, 0, 1e-6);
        Assert.InRange(speedError, 0, 1e-6);
        Assert.InRange(positionError, 0, 1e-6);
    }

    [Fact]
    public void SurfaceTravelAndSpeedFollowTheAnalyticFilmTransient()
    {
        var results = new List<object>();
        double coarsePosition = 0, coarseSpeed = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = new(.5f, 0, 0) };
            liquid.Material = liquid.Material with { SurfaceTension = 0 };
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), 1e-6, new(.02f));
            double beta = 3 * liquid.Material.Viscosity / (liquid.Material.Density * Math.Pow(liquid.Points[0].Height, 2));
            double positionError = 0, speedError = 0;
            var trace = new List<object>();
            for (int step = 1; step <= 2 * frequency; step++)
            {
                liquid.Step(1f / frequency);
                double t = liquid.Seconds, decay = Math.Exp(-beta * t);
                double speed = .5 / beta * (1 - decay), x = .5 / beta * (t - (1 - decay) / beta);
                LiquidPoint point = liquid.Points[0];
                positionError = Math.Max(positionError, Math.Abs(point.Position.X - x));
                speedError = Math.Max(speedError, Math.Abs(point.Velocity.X - speed));
                if (step % (frequency / 30) == 0) trace.Add(new { Seconds = t, X = point.Position.X, ReferenceX = x, Speed = point.Velocity.X, ReferenceSpeed = speed });
            }
            results.Add(new { Frequency = frequency, MaximumPositionErrorMetres = positionError, MaximumSpeedErrorMetresPerSecond = speedError, Trace = trace });
            if (frequency == 120) { coarsePosition = positionError; coarseSpeed = speedError; }
        }
        LiquidReferenceEvidence.Write("film-transient", new { Reference = "u=g/beta*(1-exp(-beta*t)); x=g/beta*(t-(1-exp(-beta*t))/beta)", PositionToleranceMetres = .00005, SpeedToleranceMetresPerSecond = .00001, Results = results });
        Assert.InRange(coarsePosition, 0, .00005);
        Assert.InRange(coarseSpeed, 0, .00001);
    }

    [Fact]
    public void CurvedSurfaceMotionFollowsIndependentArcIntegration()
    {
        var results = new List<object>();
        double coarsePosition = 0, coarseSpeed = 0, uncertainty = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "curved") { Pouring = false, Wetting = new(5, 30) };
            liquid.Surface = new(new(1, -.2f, .01f, 0), new(6, 0, 0, 0), new(0, 1.75f, .85f, .06f));
            float height = liquid.Surface.Height(-.5f, 0, out Vector3 normal);
            Vector3 tangent = new(normal.Y, -normal.X, 0);
            liquid.Place(new(-.5f, height, 0), 1e-6, new(.02f), tangent * .05f);
            LiquidPoint start = liquid.Points[0];
            var reference = new LiquidReference(liquid.Material.Density, liquid.Material.Viscosity, liquid.Material.SurfaceTension, 5, 30)
            { Slope = -.2f, Amplitude = .01f, Frequency = 6, GravityY = liquid.Gravity.Y };
            var value = new ReferenceDrop(start.Position.X, .05f, start.Radius, 0, start.Volume);
            ReferenceDrop fine = value;
            double positionError = 0, speedError = 0;
            var trace = new List<object>();
            for (int step = 1; step <= 2 * frequency; step++)
            {
                liquid.Step(1f / frequency);
                value = reference.Advance(value, 1f / frequency, 1.0 / 32768);
                fine = reference.Advance(fine, 1f / frequency, 1.0 / 65536);
                LiquidPoint point = liquid.Points[0];
                double speed = point.Velocity.Length();
                positionError = Math.Max(positionError, Math.Abs(point.Position.X - fine.X));
                speedError = Math.Max(speedError, Math.Abs(speed - fine.Speed));
                uncertainty = Math.Max(uncertainty, Math.Abs(value.X - fine.X));
                if (step % (frequency / 30) == 0) trace.Add(new { Seconds = liquid.Seconds, X = point.Position.X, ReferenceX = fine.X, Speed = speed, ReferenceSpeed = fine.Speed, Radius = point.Radius, ReferenceRadius = fine.Radius });
            }
            results.Add(new { Frequency = frequency, MaximumPositionErrorMetres = positionError, MaximumSpeedErrorMetresPerSecond = speedError, Trace = trace });
            if (frequency == 120) { coarsePosition = positionError; coarseSpeed = speedError; }
        }
        LiquidReferenceEvidence.Write("curved-motion", new { Reference = "Independent double RK4 on dx/dt=u/sqrt(1+f'(x)^2), tangent momentum and circular-cap shape equations", ReferenceStepsSeconds = new[] { 1.0 / 32768, 1.0 / 65536 }, ReferencePositionDifferenceMetres = uncertainty, PositionToleranceMetres = .0002, SpeedToleranceMetresPerSecond = .001, Results = results });
        Assert.InRange(uncertainty, 0, 1e-7);
        Assert.InRange(coarsePosition, 0, .0002);
        Assert.InRange(coarseSpeed, 0, .001);
    }

    [Fact]
    public void ImpactSpreadingFollowsIndependentContinuousIntegration()
    {
        var results = new List<object>();
        double coarseRadius = 0, coarseVelocity = 0, uncertainty = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero, Wetting = new(10, 10) };
            liquid.Material = liquid.Material with { Viscosity = .02f };
            liquid.Inject(new(0, LiquidFlow.TrayFloor + LiquidFlow.ContactRadius, 0), new(0, -.2f, 0), 1e-6);
            LiquidPoint start = liquid.Points[0];
            var reference = new LiquidReference(liquid.Material.Density, liquid.Material.Viscosity, liquid.Material.SurfaceTension, 10, 10);
            var value = new ReferenceDrop(0, 0, start.Radius, Math.Sqrt(3) * .2f, start.Volume);
            ReferenceDrop fine = value;
            double radiusError = 0, velocityError = 0;
            var trace = new List<object>();
            for (int step = 1; step <= 2 * frequency; step++)
            {
                liquid.Step(1f / frequency);
                value = reference.Advance(value, 1f / frequency, 1.0 / 32768);
                fine = reference.Advance(fine, 1f / frequency, 1.0 / 65536);
                LiquidPoint point = liquid.Points[0];
                radiusError = Math.Max(radiusError, Math.Abs(point.Radius - fine.Radius));
                velocityError = Math.Max(velocityError, Math.Abs(point.RadialVelocity - fine.RadialSpeed));
                uncertainty = Math.Max(uncertainty, Math.Abs(value.Radius - fine.Radius));
                if (step % (frequency / 120) == 0) trace.Add(new { Seconds = liquid.Seconds, Radius = point.Radius, ReferenceRadius = fine.Radius, RadialSpeed = point.RadialVelocity, ReferenceRadialSpeed = fine.RadialSpeed });
            }
            results.Add(new { Frequency = frequency, MaximumRadiusErrorMetres = radiusError, MaximumRadialSpeedErrorMetresPerSecond = velocityError, Trace = trace });
            if (frequency == 120) { coarseRadius = radiusError; coarseVelocity = velocityError; }
        }
        LiquidReferenceEvidence.Write("impact-spreading", new { Reference = "Normal kinetic energy to cap radial inertia; independent double RK4 of continuous cap energy and wall shear", ReferenceStepsSeconds = new[] { 1.0 / 32768, 1.0 / 65536 }, ReferenceRadiusDifferenceMetres = uncertainty, RadiusToleranceMetres = .0001, RadialSpeedToleranceMetresPerSecond = .01, Results = results });
        Assert.InRange(uncertainty, 0, 1e-7);
        Assert.InRange(coarseRadius, 0, .0001);
        Assert.InRange(coarseVelocity, 0, .01);
    }

    [Fact]
    public void RetentionStoppingAndRestartFollowAnalyticForceBalance()
    {
        var results = new List<object>();
        double coarsePosition = 0, coarseSpeed = 0, coarseStop = 0, analyticStop = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = new(.1f, -9.81f, 0), Wetting = new(5, 30) };
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), 1e-6, new(.02f), new(.05f, 0, 0));
            LiquidPoint start = liquid.Points[0];
            double beta = 3 * liquid.Material.Viscosity / (liquid.Material.Density * Math.Pow(start.Height, 2));
            double hold = 2 * start.Radius * liquid.Material.SurfaceTension *
                (Math.Cos(5 * Math.PI / 180) - Math.Cos(30 * Math.PI / 180)) * liquid.Wetting.RetentionFactor / (liquid.Material.Density * start.Volume);
            double resistance = hold - .1f;
            double stop = Math.Log(1 + beta * .05f / resistance) / beta;
            double stopX = .05f / beta - resistance * stop / beta;
            double positionError = 0, speedError = 0, observedStop = double.NaN;
            var trace = new List<object>();
            for (int step = 0; step < 2 * frequency; step++)
            {
                if (step == frequency) liquid.Gravity = new(.8f, -9.81f, 0);
                liquid.Step(1f / frequency);
                double t = liquid.Seconds, x, speed;
                if (t < stop)
                {
                    double decay = Math.Exp(-beta * t);
                    speed = (.05f + resistance / beta) * decay - resistance / beta;
                    x = (.05f + resistance / beta) * (1 - decay) / beta - resistance * t / beta;
                }
                else if (step < frequency) { x = stopX; speed = 0; }
                else
                {
                    double elapsed = t - frequency * (double)(1f / frequency), drive = .8f - hold;
                    speed = drive / beta * (1 - Math.Exp(-beta * elapsed));
                    x = stopX + drive / beta * (elapsed - (1 - Math.Exp(-beta * elapsed)) / beta);
                }
                LiquidPoint point = liquid.Points[0];
                if (double.IsNaN(observedStop) && point.Motion == LiquidMotion.Resting) observedStop = t;
                positionError = Math.Max(positionError, Math.Abs(point.Position.X - x));
                speedError = Math.Max(speedError, Math.Abs(point.Velocity.X - speed));
                if (step % (frequency / 30) == 0) trace.Add(new { Seconds = t, X = point.Position.X, ReferenceX = x, Speed = point.Velocity.X, ReferenceSpeed = speed, Motion = point.Motion.ToString() });
            }
            results.Add(new { Frequency = frequency, MaximumPositionErrorMetres = positionError, MaximumSpeedErrorMetresPerSecond = speedError, StopSeconds = observedStop, ReferenceStopSeconds = stop, RestartSeconds = frequency * (double)(1f / frequency), Trace = trace });
            if (frequency == 120) { coarsePosition = positionError; coarseSpeed = speedError; coarseStop = observedStop; analyticStop = stop; }
        }
        LiquidReferenceEvidence.Write("retention-restart", new { Reference = "Coulomb retention with exponential film drag; t_stop=log(1+beta*u0/(a_hold-g))/beta", PositionToleranceMetres = .00005, SpeedToleranceMetresPerSecond = .00001, Results = results });
        Assert.InRange(coarsePosition, 0, .00005);
        Assert.InRange(coarseSpeed, 0, .00001);
        Assert.InRange(coarseStop - analyticStop, 0, 1.0 / 120 + .00001);
    }

    [Fact]
    public void AbsorptionVolumeAndDisappearanceFollowThePrescribedFlux()
    {
        var results = new List<object>();
        double maximumError = 0, coarseGone = 0, expectedGone = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero, Wetting = new(0, 180, .001f) };
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), 1e-6, new(.02f));
            double flux = Math.PI * liquid.Points[0].Radius * liquid.Points[0].Width * liquid.Wetting.AbsorptionSpeed;
            double lifetime = liquid.Volume / flux, volume = liquid.Volume, error = 0;
            var trace = new List<object>();
            while (liquid.Count > 0 && liquid.Seconds < 2)
            {
                liquid.Step(1f / frequency);
                double expected = Math.Max(0, volume - flux * liquid.Seconds);
                error = Math.Max(error, Math.Abs(liquid.Volume - expected));
                trace.Add(new { liquid.Seconds, liquid.Volume, ReferenceVolume = expected, liquid.AbsorbedVolume, liquid.EvaporatedVolume });
            }
            results.Add(new { Frequency = frequency, MaximumVolumeErrorCubicMetres = error, DisappearanceSeconds = liquid.Seconds, ReferenceDisappearanceSeconds = lifetime, Trace = trace });
            maximumError = Math.Max(maximumError, error);
            if (frequency == 120) { coarseGone = liquid.Seconds; expectedGone = lifetime; }
        }
        LiquidReferenceEvidence.Write("absorption", new { Reference = "V(t)=max(0,V0-pi*a*b*v_abs*t)", Results = results });
        Assert.InRange(maximumError, 0, 1e-18);
        Assert.InRange(coarseGone - expectedGone, 0, 1.0 / 120 + .00001);
    }

    [Fact]
    public void PinnedDropEvaporationFollowsTheDiffusionLimitedLifetime()
    {
        var results = new List<object>();
        double maximumError = 0, coarseGone = 0, expectedGone = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero, Wetting = new(0, 180), Evaporation = new(2.5e-5f, .00865f) };
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), 1e-12, new(.0002f));
            double flux = 4 * (double)liquid.Evaporation.Diffusivity * liquid.Evaporation.VaporDensityDifference * liquid.Points[0].Radius / liquid.Material.Density;
            double lifetime = liquid.Volume / flux, volume = liquid.Volume, error = 0;
            var trace = new List<object>();
            while (liquid.Count > 0 && liquid.Seconds < 8)
            {
                liquid.Step(1f / frequency);
                double expected = Math.Max(0, volume - flux * liquid.Seconds);
                error = Math.Max(error, Math.Abs(liquid.Volume - expected));
                if ((int)(liquid.Seconds * frequency) % (frequency / 30) == 0) trace.Add(new { liquid.Seconds, liquid.Volume, ReferenceVolume = expected, liquid.AbsorbedVolume, liquid.EvaporatedVolume });
            }
            results.Add(new { Frequency = frequency, MaximumVolumeErrorCubicMetres = error, DisappearanceSeconds = liquid.Seconds, ReferenceDisappearanceSeconds = lifetime, Trace = trace });
            maximumError = Math.Max(maximumError, error);
            if (frequency == 120) { coarseGone = liquid.Seconds; expectedGone = lifetime; }
        }
        LiquidReferenceEvidence.Write("evaporation", new { Reference = "Pinned thin drop: V(t)=max(0,V0-4*D*delta_c*a*t/rho)", Results = results });
        Assert.InRange(maximumError, 0, 1e-23);
        Assert.InRange(coarseGone - expectedGone, 0, 1.0 / 120 + .00001);
    }

    [Fact]
    public void CoalescedLiquidRelaxesTowardTheYoungLaplaceDropRadius()
    {
        var results = new List<object>();
        double coarseRadius = 0, coarseEquilibrium = 0, uncertainty = 0;
        foreach (int frequency in new[] { 120, 240, 480 })
        {
            const double volume = 1e-7;
            double tangent = Math.Tan(10 * Math.PI / 360);
            float radius = (float)Math.Cbrt(6 * volume / (Math.PI * tangent * (3 + tangent * tangent)));
            var liquid = new LiquidFlow(scene: "floor") { Pouring = false, Gravity = Vector3.Zero, Wetting = new(10, 10) };
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), volume, new(radius));
            liquid.Place(new(0, LiquidFlow.TrayFloor, 0), volume, new(radius));
            var reference = new LiquidReference(liquid.Material.Density, liquid.Material.Viscosity, liquid.Material.SurfaceTension, 10, 10);
            var value = new ReferenceDrop(0, 0, radius, 0, 2 * volume);
            ReferenceDrop fine = value;
            double error = 0;
            var trace = new List<object>();
            for (int frame = 1; frame <= 240; frame++)
            {
                for (int step = 0; step < frequency / 30; step++) liquid.Step(1f / frequency);
                value = reference.Advance(value, 1.0 / 30, 1.0 / 32768);
                fine = reference.Advance(fine, 1.0 / 30, 1.0 / 65536);
                error = Math.Max(error, Math.Abs(liquid.Points[0].Radius - fine.Radius));
                uncertainty = Math.Max(uncertainty, Math.Abs(value.Radius - fine.Radius));
                trace.Add(new { Seconds = frame / 30.0, Radius = liquid.Points[0].Radius, ReferenceRadius = fine.Radius, liquid.Volume });
            }
            double equilibrium = Math.Cbrt(12 * volume / (Math.PI * tangent * (3 + tangent * tangent)));
            double equilibriumError = Math.Abs(liquid.Points[0].Radius / equilibrium - 1);
            results.Add(new { Frequency = frequency, MaximumRadiusErrorMetres = error, YoungLaplaceRelativeRadiusError = equilibriumError, Trace = trace });
            if (frequency == 120) { coarseRadius = error; coarseEquilibrium = equilibriumError; }
        }
        LiquidReferenceEvidence.Write("coalescence-relaxation", new { Reference = "Independent continuous cap relaxation after coincident-volume coalescence; equilibrium from spherical-cap Young-Laplace geometry", ReferenceRadiusDifferenceMetres = uncertainty, RadiusToleranceMetres = .0001, EquilibriumRelativeRadiusTolerance = .01, Results = results });
        Assert.InRange(uncertainty, 0, 1e-7);
        Assert.InRange(coarseRadius, 0, .0001);
        Assert.InRange(coarseEquilibrium, 0, .01);
    }
}
