using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationPose(Vector3 Position, float Heading);

/// <summary>A constant signed speed and curvature applied for Duration seconds.</summary>
public readonly record struct NavigationVehicleControl(float Speed, float Curvature, float Duration);

/// <summary>Kinematic car motion in XZ. Curvature is inverse metres; positive curvature turns toward +X.</summary>
public readonly record struct NavigationVehicleModel
{
    public NavigationVehicleModel(float minimumTurningRadius, bool allowReverse = false)
    {
        MinimumTurningRadius = minimumTurningRadius;
        AllowReverse = allowReverse;
    }

    public float MinimumTurningRadius { get; }
    public bool AllowReverse { get; }
    public float MaximumCurvature => 1 / MinimumTurningRadius;

    public NavigationPose Integrate(NavigationPose pose, NavigationVehicleControl control, float deltaTime)
    {
        float distance = control.Speed * deltaTime;
        if (MathF.Abs(control.Curvature) < 0.000001f)
            return pose with { Position = pose.Position + NavigationHeading.Direction(pose.Heading) * distance };
        float heading = pose.Heading + control.Curvature * distance;
        Vector3 movement = new((MathF.Cos(pose.Heading) - MathF.Cos(heading)) / control.Curvature, 0,
            (MathF.Sin(heading) - MathF.Sin(pose.Heading)) / control.Curvature);
        return new(pose.Position + movement, MathF.IEEERemainder(heading, 2 * MathF.PI));
    }
}

public readonly record struct NavigationMotionSample(float Time, NavigationPose Pose, Vector3 Velocity);
public readonly record struct NavigationMotionSweepTriangle(Vector3 A, Vector3 B, Vector3 C);

public readonly record struct NavigationMotionPrimitive(int StartHeading, int EndHeading,
    NavigationPose Start, NavigationPose End, int ControlStart, int ControlCount,
    int SampleStart, int SampleCount, int SweepStart, int SweepCount);

public readonly record struct NavigationMotionSegment(NavigationPose Start, NavigationPose End, NavigationVehicleControl Control);

/// <summary>Completed lattice primitives, their controls, timed samples and swept body geometry.</summary>
public sealed class NavigationMotionSet(NavigationVehicleModel model, float resolution,
    ReadOnlyMemory<float> headings, ReadOnlyMemory<NavigationMotionPrimitive> primitives,
    ReadOnlyMemory<NavigationVehicleControl> controls, ReadOnlyMemory<NavigationMotionSample> samples,
    ReadOnlyMemory<NavigationMotionSweepTriangle> sweptGeometry)
{
    public NavigationVehicleModel Model { get; } = model;
    public float Resolution { get; } = resolution;
    public ReadOnlyMemory<float> Headings { get; } = headings;
    public ReadOnlyMemory<NavigationMotionPrimitive> Primitives { get; } = primitives;
    public ReadOnlyMemory<NavigationVehicleControl> Controls { get; } = controls;
    public ReadOnlyMemory<NavigationMotionSample> Samples { get; } = samples;
    public ReadOnlyMemory<NavigationMotionSweepTriangle> SweptGeometry { get; } = sweptGeometry;

    /// <summary>Builds a four-heading lattice with straight steps and quarter-circle turns on lattice coordinates.</summary>
    public static NavigationMotionSet CreateCarLattice(NavigationProfile profile, float resolution, float speed)
    {
        NavigationVehicleModel model = profile.VehicleModel!.Value;
        speed = MathF.Min(speed, profile.MaximumSpeed);
        float radius = MathF.Ceiling(model.MinimumTurningRadius / resolution) * resolution;
        float[] headings = [0, MathF.PI / 2, MathF.PI, -MathF.PI / 2];
        var primitives = new List<NavigationMotionPrimitive>();
        var controls = new List<NavigationVehicleControl>();
        var samples = new List<NavigationMotionSample>();
        var swept = new List<NavigationMotionSweepTriangle>();
        for (int heading = 0; heading < headings.Length; heading++)
        foreach (float direction in model.AllowReverse ? new float[] { 1, -1 } : [1])
        {
            AddPrimitive(heading, new(direction * speed, 0, resolution / speed));
            AddPrimitive(heading, new(direction * speed, 1 / radius, MathF.PI * radius / (2 * speed)));
            AddPrimitive(heading, new(direction * speed, -1 / radius, MathF.PI * radius / (2 * speed)));
        }
        return new(model, resolution, headings, primitives.ToArray(), controls.ToArray(), samples.ToArray(), swept.ToArray());

        void AddPrimitive(int heading, NavigationVehicleControl control)
        {
            if (control.Curvature != 0)
            {
                float turnSpeed = MathF.Min(MathF.Abs(control.Speed), profile.MaximumAngularSpeed / MathF.Abs(control.Curvature));
                control = control with
                {
                    Speed = MathF.CopySign(turnSpeed, control.Speed),
                    Duration = control.Duration * MathF.Abs(control.Speed) / turnSpeed,
                };
            }
            var start = new NavigationPose(Vector3.Zero, headings[heading]);
            NavigationPose end = model.Integrate(start, control, control.Duration);
            int endHeading = (int)MathF.Round(end.Heading / (MathF.PI / 2));
            endHeading = (endHeading % 4 + 4) % 4;
            int firstControl = controls.Count, firstSample = samples.Count, firstSweep = swept.Count;
            controls.Add(control);
            int count = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(control.Speed) * control.Duration / (resolution / 4)));
            Vector3[] previous = [];
            for (int index = 0; index <= count; index++)
            {
                float time = control.Duration * index / count;
                NavigationPose pose = model.Integrate(start, control, time);
                samples.Add(new(time, pose, NavigationHeading.Direction(pose.Heading) * control.Speed));
                var body = new Vector3[profile.Footprint.Length];
                for (int vertex = 0; vertex < body.Length; vertex++)
                {
                    Vector2 point = profile.Footprint.Span[vertex];
                    body[vertex] = pose.Position + new Vector3(
                        MathF.Cos(pose.Heading) * point.X + MathF.Sin(pose.Heading) * point.Y, 0,
                        -MathF.Sin(pose.Heading) * point.X + MathF.Cos(pose.Heading) * point.Y);
                }
                for (int vertex = 1; vertex < body.Length - 1; vertex++)
                    swept.Add(new(body[0], body[vertex], body[vertex + 1]));
                if (index > 0)
                    for (int vertex = 0; vertex < body.Length; vertex++)
                    {
                        int next = (vertex + 1) % body.Length;
                        swept.Add(new(previous[vertex], previous[next], body[next]));
                        swept.Add(new(previous[vertex], body[next], body[vertex]));
                    }
                previous = body;
            }
            primitives.Add(new(heading, endHeading, start, end, firstControl, 1,
                firstSample, samples.Count - firstSample, firstSweep, swept.Count - firstSweep));
        }
    }
}
