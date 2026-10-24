using System.Numerics;

namespace SomeEngine.Navigation;

internal static class NavigationMppiSampling<TSpace, TModel>
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    internal static void Candidate(NavigationMppiData<TSpace> data, int candidate, int iteration)
    {
        NavigationMppiFrame<TSpace> frame = data.Frame;
        NavigationMppiSettings settings = frame.Settings;
        int steps = settings.HorizonSteps, start = candidate * steps;
        ulong random = settings.Seed + (ulong)(data.Sequence + 1) * 1442695040888963407UL +
            (ulong)(frame.Actor + 1) * 6364136223846793005UL + (ulong)(candidate + 1) * 2862933555777941757UL + (uint)iteration;
        Vector2 common = Gaussian(ref random) * MathF.Sqrt(settings.NoiseCorrelation);
        float independent = MathF.Sqrt(1 - settings.NoiseCorrelation);
        Vector2 noiseSum = default, controlSum = default, productSum = default;
        for (int step = 0; step < steps; step++)
        {
            Vector2 noise = settings.NoiseStandardDeviation * (common + independent * Gaussian(ref random));
            data.Noise[start + step] = noise;
            data.CandidateControls[start + step] = data.Controls[step] + noise;
            noiseSum += noise;
            controlSum += data.Controls[step];
            productSum += data.Controls[step] * noise;
        }
        // Inverse of sigma² [(1-rho) I + rho 11ᵀ], including the control/noise likelihood term.
        float correlation = settings.NoiseCorrelation / (1 - settings.NoiseCorrelation + steps * settings.NoiseCorrelation);
        Vector2 likelihood = (productSum - correlation * controlSum * noiseSum) / (1 - settings.NoiseCorrelation);
        float correction = 0;
        if (settings.NoiseStandardDeviation.X > 0) correction += likelihood.X / (settings.NoiseStandardDeviation.X * settings.NoiseStandardDeviation.X);
        if (settings.NoiseStandardDeviation.Y > 0) correction += likelihood.Y / (settings.NoiseStandardDeviation.Y * settings.NoiseStandardDeviation.Y);
        float cost = Evaluate(data, data.CandidateControls.AsSpan(start, steps),
            data.CandidateMotions.AsSpan(candidate * (steps + 1), steps + 1));
        data.Scores[candidate] = cost + settings.ControlWeight * correction;
    }

    internal static void Improve(NavigationMppiData<TSpace> data)
    {
        NavigationMppiFrame<TSpace> frame = data.Frame;
        NavigationMppiSettings settings = frame.Settings;
        int best = -1;
        float minimum = float.PositiveInfinity;
        for (int candidate = 0; candidate < settings.CandidateCount; candidate++)
            if (data.Scores[candidate] < minimum) { minimum = data.Scores[candidate]; best = candidate; }
        data.Controls.CopyTo(data.UpdatedControls, 0);
        if (best >= 0)
        {
            float sum = 0;
            for (int candidate = 0; candidate < settings.CandidateCount; candidate++)
            {
                float weight = MathF.Exp(-(data.Scores[candidate] - minimum) / settings.Temperature);
                data.Weights[candidate] = weight;
                sum += weight;
            }
            for (int step = 0; step < settings.HorizonSteps; step++)
            {
                Vector2 update = default;
                for (int candidate = 0; candidate < settings.CandidateCount; candidate++)
                    update += data.Weights[candidate] / sum * data.Noise[candidate * settings.HorizonSteps + step];
                data.UpdatedControls[step] += update;
            }
        }
        float selected = Evaluate(data, data.UpdatedControls, data.SelectedMotions);
        if (!float.IsFinite(selected) && best >= 0)
        {
            Array.Copy(data.CandidateControls, best * settings.HorizonSteps, data.UpdatedControls, 0, settings.HorizonSteps);
            Array.Copy(data.CandidateMotions, best * (settings.HorizonSteps + 1), data.SelectedMotions, 0, settings.HorizonSteps + 1);
            selected = data.Scores[best];
        }
        if (!float.IsFinite(selected))
        {
            Array.Clear(data.UpdatedControls);
            selected = Evaluate(data, data.UpdatedControls, data.SelectedMotions);
        }
        data.SelectedCost = selected;
        data.Valid = float.IsFinite(selected);
        data.SelectedCount = data.Valid ? settings.HorizonSteps + 1 : 1;
        data.SelectedMotions[0] = frame.Actual;
        if (data.Valid)
        {
            data.UpdatedControls.CopyTo(data.Controls, 0);
            data.UpdatedControls.CopyTo(data.SelectedControls, 0);
            NavigationMotion actual = frame.Actual;
            NavigationGuidance guidance = frame.Guidance;
            data.Steering = TModel.Steering(data.SelectedControls[0], in actual, in data.SelectedMotions[1],
                frame.Profile, in guidance, settings.TimeStep);
        }
        else
        {
            data.Steering = new(default, frame.Actual.Heading, frame.Guidance.RequestVersion, false);
            data.Initialized = false;
        }
    }

    private static float Evaluate(NavigationMppiData<TSpace> data, Span<Vector2> controls, Span<NavigationMotion> trajectory)
    {
        NavigationMppiFrame<TSpace> frame = data.Frame;
        NavigationMppiSettings settings = frame.Settings;
        NavigationMotion actual = frame.Actual;
        trajectory[0] = actual;
        float total = 0;
        for (int step = 0; step < settings.HorizonSteps; step++)
        {
            Vector2 control = TModel.Constrain(controls[step], in actual, frame.Profile, settings.TimeStep);
            controls[step] = control;
            if (!TModel.TryStep(frame.Space, in actual, control, frame.Profile, frame.Policy, settings.TimeStep,
                out NavigationMotion next, out float traversal)) return float.PositiveInfinity;
            float neighbor = NeighborCost(frame, in actual, in next, control, step * settings.TimeStep);
            if (!float.IsFinite(neighbor)) return neighbor;
            NavigationPose reference = data.Reference[step + 1];
            float heading = NavigationHeading.Difference(reference.Heading, next.Heading);
            total += settings.TimeStep * (settings.PathWeight * Vector3.DistanceSquared(next.Position, reference.Position) +
                settings.HeadingWeight * heading * heading + settings.NeighborWeight * neighbor) + settings.TraversalWeight * traversal;
            trajectory[step + 1] = next;
            actual = next;
        }
        float goalDistance = MathF.Max(0, Vector3.Distance(actual.Position, frame.Target) - frame.Goal.ArrivalRange);
        total += settings.GoalWeight * goalDistance * goalDistance;
        if (goalDistance < 1 && frame.Goal.Heading is { } goalHeading)
        {
            float heading = NavigationHeading.Difference(goalHeading, actual.Heading);
            total += settings.HeadingWeight * heading * heading;
        }
        return total;
    }

    private static float NeighborCost(NavigationMppiFrame<TSpace> frame, in NavigationMotion first, in NavigationMotion last,
        Vector2 control, float elapsed)
    {
        float total = 0;
        NavigationNeighborAgent self = frame.Neighbors.Agents.Span[frame.Actor];
        float bodyRadius = NavigationSurfaceTraversal.GetRotationRadius(frame.Profile.Footprint.Span);
        foreach (int index in frame.Neighbors.GetNeighbors(frame.Actor).Span)
        {
            NavigationNeighborAgent other = frame.Neighbors.Agents.Span[index];
            NavigationMotion otherFirst = frame.Neighbors.Predict(index, frame.Time + elapsed, frame.Time);
            NavigationMotion otherLast = frame.Neighbors.Predict(index, frame.Time + elapsed + frame.Settings.TimeStep, frame.Time);
            float travel = Vector3.Distance(first.Position, last.Position) + Vector3.Distance(otherFirst.Position, otherLast.Position) +
                bodyRadius * MathF.Abs(NavigationHeading.Difference(last.Heading, first.Heading));
            int samples = Math.Max(1, (int)MathF.Ceiling(travel / frame.Settings.CollisionResolution));
            for (int sample = 0; sample <= samples; sample++)
            {
                float fraction = (float)sample / samples;
                float time = frame.Settings.TimeStep * fraction;
                NavigationMotion own = TModel.Integrate(in first, control, frame.Profile, time);
                own = own with { Position = own.Position with { Y = first.Position.Y + (last.Position.Y - first.Position.Y) * fraction } };
                NavigationMotion predicted = frame.Neighbors.Predict(index, frame.Time + elapsed + time, frame.Time);
                if (own.Position.Y + frame.Profile.Height <= predicted.Position.Y ||
                    predicted.Position.Y + other.Profile.Height <= own.Position.Y) continue;
                Vector3 offset = predicted.Position - own.Position;
                Vector2 local = new(MathF.Cos(own.Heading) * offset.X - MathF.Sin(own.Heading) * offset.Z,
                    MathF.Sin(own.Heading) * offset.X + MathF.Cos(own.Heading) * offset.Z);
                float clearance = FootprintDistance(local, frame.Profile.Footprint.Span) - other.Radius - self.Avoidance.Spacing;
                if (clearance < 0) return float.PositiveInfinity;
                total += MathF.Exp(-clearance / MathF.Max(other.Radius, 0.1f)) / (samples + 1);
            }
        }
        return total;
    }

    private static float FootprintDistance(Vector2 point, ReadOnlySpan<Vector2> footprint)
    {
        float distance = float.PositiveInfinity;
        bool inside = false;
        for (int index = 0; index < footprint.Length; index++)
        {
            Vector2 first = footprint[index], second = footprint[(index + 1) % footprint.Length];
            Vector2 edge = second - first;
            float fraction = Math.Clamp(Vector2.Dot(point - first, edge) / edge.LengthSquared(), 0, 1);
            distance = MathF.Min(distance, Vector2.DistanceSquared(point, first + fraction * edge));
            if ((first.Y > point.Y) != (second.Y > point.Y) &&
                point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X) inside = !inside;
        }
        return (inside ? -1 : 1) * MathF.Sqrt(distance);
    }

    private static Vector2 Gaussian(ref ulong state)
    {
        double first = Uniform(ref state), second = Uniform(ref state);
        double radius = Math.Sqrt(-2 * Math.Log(first)), angle = 2 * Math.PI * second;
        return new((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
    }

    private static double Uniform(ref ulong state)
    {
        state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
        return ((state >> 11) + 0.5) / 9007199254740992.0;
    }
}
