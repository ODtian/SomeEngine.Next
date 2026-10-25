using System.Numerics;

namespace SomeEngine.Liquid;

public sealed partial class LiquidFlow
{
    private void Advance(ref LiquidPoint point, float seconds)
    {
        double remaining = seconds;
        while (remaining > 0)
        {
            double speed = point.Velocity.Length();
            double dt = Math.Min(remaining, Surface.Ripple.W * .4 / (speed + Math.Sqrt(Gravity.Length() * Surface.Ripple.W)));
            if (point.Surface == LiquidContactSurface.None)
            {
                ResolveContact(ref point);
                if (point.Surface == LiquidContactSurface.None)
                {
                    Vector3 start = point.Position, velocity = point.Velocity;
                    Vector3 end = start + velocity * (float)dt + .5f * Gravity * (float)(dt * dt);
                    if (Distance(end, out _) > ContactRadius)
                    {
                        point.Position = end; point.Velocity += Gravity * (float)dt;
                        remaining -= dt;
                        continue;
                    }
                    double lower = 0, upper = dt;
                    for (int iteration = 0; iteration < 24; iteration++)
                    {
                        double middle = .5 * (lower + upper);
                        Vector3 position = start + velocity * (float)middle + .5f * Gravity * (float)(middle * middle);
                        if (Distance(position, out _) > ContactRadius) lower = middle; else upper = middle;
                    }
                    point.Position = start + velocity * (float)upper + .5f * Gravity * (float)(upper * upper);
                    point.Velocity += Gravity * (float)upper;
                    remaining -= upper;
                    ResolveContact(ref point);
                    continue;
                }
            }
            ShapeForces forces = GetShapeForces(point, point.Radius, point.Width);
            if (point.RadialVelocity != 0 || point.WidthVelocity != 0 ||
                Math.Abs(forces.RadiusForce) > forces.RadiusRetention || Math.Abs(forces.WidthForce) > forces.WidthRetention)
                dt = Math.Min(dt, .08 / forces.CapillaryFrequency);
            Spread(ref point, dt * .5);
            AdvanceSurface(ref point, dt);
            if (point.Surface != LiquidContactSurface.None) Spread(ref point, dt * .5);
            remaining -= dt;
        }
    }

    private void AdvanceSurface(ref LiquidPoint point, double seconds)
    {
        Vector3 normal = point.Normal;
        Vector3 tangent = point.Velocity - normal * Vector3.Dot(point.Velocity, normal);
        bool onFace = ContactDistance(point.Position + tangent * (float)seconds, point.Surface, out _, out Vector3 nextNormal);
        double outward = Vector3.Dot(Gravity, normal) + Vector3.Dot(tangent, nextNormal - normal) / seconds;
        double adhesion = 2 * Math.PI * Math.Sqrt(.5 * (point.Radius * point.Radius + point.Width * point.Width)) * Material.SurfaceTension *
            Math.Sqrt(1 - Wetting.EquilibriumCosine * Wetting.EquilibriumCosine) / (Material.Density * point.Volume);
        if (!onFace || outward > adhesion)
        {
            point.Surface = LiquidContactSurface.None; point.Normal = Vector3.Zero;
            point.Position += point.Velocity * (float)seconds + .5f * Gravity * (float)(seconds * seconds);
            point.Velocity += Gravity * (float)seconds;
            ResolveContact(ref point);
            return;
        }
        double height = point.Volume / (Math.PI * point.Radius * point.Width);
        double drag = 3 * Material.Viscosity / (Material.Density * height * height);
        Vector3 drive = CalculateSurfaceForces(point, point.Radius, point.Width).Acceleration;
        double retention = CalculateSurfaceRetention(point, normal, tangent + drive * (float)seconds);
        var half = IntegrateSurfaceMotion(tangent, drive, drag, retention, seconds * .5);
        ContactDistance(point.Position + half.Distance, point.Surface, out _, out Vector3 middleNormal);
        Vector3 middleVelocity = TransportTangent(tangent, middleNormal);
        LiquidPoint middle = point;
        middle.Position += half.Distance; middle.Normal = middleNormal;
        middle.Axis = TransportTangent(point.Axis, middleNormal);
        drive = CalculateSurfaceForces(middle, point.Radius, point.Width).Acceleration;
        retention = CalculateSurfaceRetention(point, middleNormal, middleVelocity + drive * (float)seconds);
        var full = IntegrateSurfaceMotion(middleVelocity, drive, drag, retention, seconds);
        if (full.Distance == Vector3.Zero && full.Velocity == Vector3.Zero) { point.Velocity = Vector3.Zero; return; }
        point.Position += full.Distance;
        ContactDistance(point.Position, point.Surface, out float separation, out nextNormal);
        point.Position += nextNormal * (ContactRadius - separation);
        point.Velocity = TransportTangent(full.Velocity, nextNormal);
        point.Normal = nextNormal;
        point.Axis = Vector3.Normalize(point.Axis - nextNormal * Vector3.Dot(point.Axis, nextNormal));
        ContainInTray(ref point);
    }

    private double CalculateSurfaceRetention(in LiquidPoint point, Vector3 normal, Vector3 motion)
    {
        Vector3 across = motion.LengthSquared() > 0 ? Vector3.Normalize(Vector3.Cross(normal, motion)) : point.Axis;
        return 2 * ContactReach(point, across) * Material.SurfaceTension * Wetting.CosineDifference * Wetting.RetentionFactor / (Material.Density * point.Volume);
    }

    private static Vector3 TransportTangent(Vector3 velocity, Vector3 normal)
    {
        Vector3 tangent = velocity - normal * Vector3.Dot(velocity, normal);
        return tangent.LengthSquared() > 0 ? Vector3.Normalize(tangent) * velocity.Length() : Vector3.Zero;
    }

    private static (Vector3 Distance, Vector3 Velocity) IntegrateSurfaceMotion(Vector3 velocity, Vector3 drive, double drag, double retention, double seconds)
    {
        if (velocity == Vector3.Zero && drive.Length() <= retention) return (Vector3.Zero, Vector3.Zero);
        double halfResponse = CalculateDragResponse(drag, seconds * .5);
        Vector3 middle = velocity * (float)Math.Exp(-drag * seconds * .5) + drive * (float)halfResponse;
        if (middle.LengthSquared() == 0) return (Vector3.Zero, Vector3.Zero);
        Vector3 direction = Vector3.Normalize(middle);
        double speed = Vector3.Dot(velocity, direction), acceleration = Vector3.Dot(drive, direction);
        var along = IntegrateDampedMotion(speed, acceleration, drag, retention, seconds);
        Vector3 acrossVelocity = velocity - direction * (float)speed, acrossDrive = drive - direction * (float)acceleration;
        double response = CalculateDragResponse(drag, seconds), forceDistance = CalculateForceDistance(drag, seconds, response);
        Vector3 distance = direction * (float)along.Distance + acrossVelocity * (float)response + acrossDrive * (float)forceDistance;
        Vector3 nextVelocity = along.Velocity == 0 && drive.Length() <= retention ? Vector3.Zero :
            direction * (float)along.Velocity + acrossVelocity * (float)Math.Exp(-drag * seconds) + acrossDrive * (float)response;
        return (distance, nextVelocity);
    }

    // Integrate v' = acceleration - drag*v - retention*sign(v), including its stopping event.
    private static (double Distance, double Velocity) IntegrateDampedMotion(double velocity, double acceleration, double drag, double retention, double seconds)
    {
        if (velocity == 0 && Math.Abs(acceleration) <= retention) return (0, 0);
        double direction = Math.Sign(velocity == 0 ? acceleration : velocity);
        double drive = acceleration - direction * retention, duration = seconds;
        if (velocity != 0 && direction * drive < 0)
        {
            double stop = drag > 0 ? Math.Log(1 - drag * velocity / drive) / drag : -velocity / drive;
            duration = Math.Min(seconds, stop);
        }
        double response = CalculateDragResponse(drag, duration);
        double distance = velocity * response + drive * CalculateForceDistance(drag, duration, response);
        double nextVelocity = velocity * Math.Exp(-drag * duration) + drive * response;
        if (duration < seconds)
        {
            var after = IntegrateDampedMotion(0, acceleration, drag, retention, seconds - duration);
            return (distance + after.Distance, after.Velocity);
        }
        return (distance, nextVelocity);
    }

    private static double CalculateDragResponse(double drag, double seconds) => drag > 0 ? (1 - Math.Exp(-drag * seconds)) / drag : seconds;
    private static double CalculateForceDistance(double drag, double seconds, double response) => drag > 0 ? (seconds - response) / drag : .5 * seconds * seconds;

    private readonly record struct ShapeForces(double RadiusForce, double WidthForce, double Drag, double RadiusRetention, double WidthRetention, double CapillaryFrequency);

    private readonly record struct SurfaceForces(Vector3 Acceleration, double RadiusForce, double WidthForce);

    private SurfaceForces CalculateSurfaceForces(in LiquidPoint point, double radius, double width)
    {
        if (point.Surface != LiquidContactSurface.CurvedTop)
            return new(Gravity - point.Normal * Vector3.Dot(Gravity, point.Normal), 0, 0);
        ReadOnlySpan<double> radialPoints = [.1127016653792583, .5, .8872983346207417];
        ReadOnlySpan<double> radialWeights = [5.0 / 18, 4.0 / 9, 5.0 / 18];
        Vector3 centre = point.Position - point.Normal * ContactRadius, across = Vector3.Cross(point.Normal, point.Axis);
        double driveX = 0, driveZ = 0, radiusForce = 0, widthForce = 0;
        for (int radial = 0; radial < radialPoints.Length; radial++)
        for (int angular = 0; angular < 8; angular++)
        {
            double root = Math.Sqrt(radialPoints[radial]), angle = angular * (Math.PI / 4);
            double u = root * Math.Cos(angle), v = root * Math.Sin(angle);
            Vector3 sample = centre + point.Axis * (float)(radius * u) + across * (float)(width * v);
            Surface.Height(sample.X, sample.Z, out Vector3 normal);
            Vector2 curvatureGradient = Surface.CalculateLaplacianGradient(sample.X, sample.Z);
            double fx = Gravity.X - Gravity.Y * normal.X / normal.Y + Material.SurfaceTension / Material.Density * curvatureGradient.X;
            double fz = Gravity.Z - Gravity.Y * normal.Z / normal.Y + Material.SurfaceTension / Material.Density * curvatureGradient.Y;
            double weight = 2 * (1 - radialPoints[radial]) * radialWeights[radial] / 8;
            driveX += weight * fx; driveZ += weight * fz;
            radiusForce += weight * (fx * point.Axis.X + fz * point.Axis.Z) * u;
            widthForce += weight * (fx * across.X + fz * across.Z) * v;
        }
        Vector3 drive = new((float)driveX, 0, (float)driveZ);
        drive -= point.Normal * Vector3.Dot(drive, point.Normal);
        double mass = Material.Density * point.Volume;
        return new(drive, mass * radiusForce, mass * widthForce);
    }

    private ShapeForces GetShapeForces(in LiquidPoint point, double radius, double width)
    {
        double volume = point.Volume, a2 = radius * radius, b2 = width * width, a3 = a2 * radius, b3 = b2 * width, a4 = a2 * a2, b4 = b2 * b2;
        double mass = Material.Density * volume / 6;
        double wetting = Math.PI * Material.SurfaceTension * (1 - Wetting.EquilibriumCosine);
        double capillary = 2 * Material.SurfaceTension * volume * volume / Math.PI;
        double gravity = 2 * Material.Density * Math.Max(0, -Vector3.Dot(Gravity, point.Normal)) * volume * volume / (3 * Math.PI);
        double radiusForce = -wetting * width + capillary * (3 / (a4 * width) + 1 / (a2 * b3)) + gravity / (a2 * width);
        double widthForce = -wetting * radius + capillary * (1 / (a3 * b2) + 3 / (radius * b4)) + gravity / (radius * b2);
        SurfaceForces geometry = CalculateSurfaceForces(point, radius, width);
        radiusForce += geometry.RadiusForce; widthForce += geometry.WidthForce;
        double haa = capillary * (12 / (a4 * radius * width) + 2 / (a3 * b3)) + 2 * gravity / (a3 * width);
        double hbb = capillary * (2 / (a3 * b3) + 12 / (radius * b4 * width)) + 2 * gravity / (radius * b3);
        double hab = wetting + capillary * (3 / (a4 * b2) + 3 / (a2 * b4)) + gravity / (a2 * b2);
        double frequency = Math.Sqrt((Math.Max(haa, hbb) + Math.Abs(hab)) / mass);
        double drag = .75 * Material.Viscosity * Math.PI * Math.PI * a2 * b2 / volume;
        double holding = .5 * Math.PI * Material.SurfaceTension * Wetting.CosineDifference;
        return new(radiusForce, widthForce, drag, holding * width, holding * radius, frequency);
    }

    private void Spread(ref LiquidPoint point, double seconds)
    {
        // Exponential midpoint integrates wall shear analytically and evaluates cap forces at mid-step.
        // The energy Hessian sets the capillary time used by Advance for the coupled motion and shape.
        double radius = point.Radius, width = point.Width, mass = Material.Density * point.Volume / 6;
        ShapeForces initial = GetShapeForces(point, radius, width);
        var halfRadius = IntegrateDampedMotion(point.RadialVelocity, initial.RadiusForce / mass, initial.Drag / mass, initial.RadiusRetention / mass, seconds * .5);
        var halfWidth = IntegrateDampedMotion(point.WidthVelocity, initial.WidthForce / mass, initial.Drag / mass, initial.WidthRetention / mass, seconds * .5);
        ShapeForces middle = GetShapeForces(point, radius + halfRadius.Distance, width + halfWidth.Distance);
        var fullRadius = IntegrateDampedMotion(point.RadialVelocity, middle.RadiusForce / mass, middle.Drag / mass, middle.RadiusRetention / mass, seconds);
        var fullWidth = IntegrateDampedMotion(point.WidthVelocity, middle.WidthForce / mass, middle.Drag / mass, middle.WidthRetention / mass, seconds);
        point.Radius = (float)(radius + fullRadius.Distance); point.Width = (float)(width + fullWidth.Distance);
        point.RadialVelocity = (float)fullRadius.Velocity; point.WidthVelocity = (float)fullWidth.Velocity;
    }
}
