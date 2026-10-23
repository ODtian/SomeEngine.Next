using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationMppiReference<TSpace, TModel>
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    internal static void Prepare(NavigationMppiData<TSpace> data, NavigationPathResult path)
    {
        NavigationMppiFrame<TSpace> frame = data.Frame;
        NavigationMppiSettings settings = frame.Settings;
        int steps = settings.HorizonSteps;
        bool changed = data.Version != path.RequestVersion || !ReferenceEquals(data.Path, path.Path);
        if (changed) data.ReferenceIndex = 0;
        FillReference(data, path.Path);
        NavigationMotion predicted = frame.Actual;
        int lookahead = Math.Max(1, (int)MathF.Ceiling(settings.ReferenceLookahead / MathF.Max(frame.DesiredSpeed * settings.TimeStep, 0.01f)));
        for (int step = 0; step < steps; step++)
        {
            Vector2 control = TModel.ReferenceControl(in predicted, data.Reference[Math.Min(steps, step + lookahead)],
                data.ReferenceSpeeds[step], frame.Profile);
            control = TModel.Constrain(control, in predicted, frame.Profile, settings.TimeStep);
            data.UpdatedControls[step] = control;
            predicted = TModel.Integrate(in predicted, control, frame.Profile, settings.TimeStep);
        }

        if (!data.Initialized || changed)
        {
            data.UpdatedControls.CopyTo(data.Controls, 0);
            data.ShiftTime = 0;
        }
        else
        {
            float elapsed = data.ShiftTime + frame.Time - data.LastTime;
            int shift = Math.Min(steps, (int)MathF.Floor((elapsed + 0.000001f) / settings.TimeStep));
            data.ShiftTime = MathF.Max(0, elapsed - shift * settings.TimeStep);
            if (shift > 0)
            {
                Array.Copy(data.Controls, shift, data.Controls, 0, steps - shift);
                Array.Copy(data.UpdatedControls, steps - shift, data.Controls, steps - shift, shift);
            }
        }
        data.LastTime = frame.Time;
        data.Version = path.RequestVersion;
        data.Path = path.Path;
        data.Sequence++;
        data.Initialized = true;
    }

    private static void FillReference(NavigationMppiData<TSpace> data, NavigationPath? path)
    {
        NavigationMppiFrame<TSpace> frame = data.Frame;
        int steps = frame.Settings.HorizonSteps;
        float distanceToGoal = Vector3.Distance(frame.Actual.Position, frame.Target);
        bool arrived = distanceToGoal <= frame.Goal.ArrivalRange + 0.0001f &&
            (frame.Goal.Heading is not { } heading || MathF.Abs(NavigationHeading.Difference(heading, frame.Actual.Heading)) <= frame.Goal.HeadingTolerance);
        int count = path is null ? 0 : path.Poses.IsEmpty ? path.Corners.Length : path.Poses.Length;
        if (arrived || count == 0)
        {
            Vector3 velocity = arrived ? default : frame.Guidance.Velocity;
            for (int step = 0; step <= steps; step++)
                data.Reference[step] = new(frame.Actual.Position + velocity * (step * frame.Settings.TimeStep), frame.Guidance.Heading);
        }
        else
        {
            int nearest = data.ReferenceIndex;
            float closest = float.PositiveInfinity;
            for (int index = Math.Max(0, data.ReferenceIndex - 1); index < count; index++)
            {
                NavigationPose pose = Pose(index);
                float angle = NavigationHeading.Difference(pose.Heading, frame.Actual.Heading);
                float distance = Vector3.DistanceSquared(pose.Position, frame.Actual.Position) + 0.1f * angle * angle;
                if (distance >= closest) continue;
                closest = distance;
                nearest = index;
            }
            data.ReferenceIndex = nearest;
            int segment = nearest;
            float traveled = 0;
            for (int step = 0; step <= steps; step++)
            {
                float requested = frame.DesiredSpeed * step * frame.Settings.TimeStep;
                while (segment < count - 1)
                {
                    float length = Vector3.Distance(Pose(segment).Position, Pose(segment + 1).Position);
                    if (length > 0 && traveled + length >= requested) break;
                    traveled += length;
                    segment++;
                }
                NavigationPose first = Pose(segment);
                if (segment == count - 1) data.Reference[step] = first;
                else
                {
                    NavigationPose second = Pose(segment + 1);
                    float fraction = Math.Clamp((requested - traveled) / Vector3.Distance(first.Position, second.Position), 0, 1);
                    data.Reference[step] = new(Vector3.Lerp(first.Position, second.Position, fraction),
                        first.Heading + NavigationHeading.Difference(second.Heading, first.Heading) * fraction);
                }
            }
        }
        for (int step = 0; step < steps; step++)
        {
            Vector3 displacement = data.Reference[step + 1].Position - data.Reference[step].Position;
            float speed = MathF.Min(frame.DesiredSpeed, displacement.Length() / frame.Settings.TimeStep);
            if (Vector3.Distance(data.Reference[step].Position, frame.Target) <= frame.Goal.ArrivalRange) speed = 0;
            data.ReferenceSpeeds[step] = MathF.CopySign(speed, Vector3.Dot(displacement, NavigationHeading.Direction(data.Reference[step].Heading)));
        }

        NavigationPose Pose(int index)
        {
            if (!path!.Poses.IsEmpty) return path.Poses.Span[index];
            Vector3 point = path.Corners.Span[index];
            float heading = index < path.Corners.Length - 1 ? NavigationHeading.FromDirection(path.Corners.Span[index + 1] - point)
                : frame.Goal.Heading ?? frame.Guidance.Heading;
            return new(point, heading);
        }
    }
}
