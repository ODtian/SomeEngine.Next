using System.Diagnostics;
using System.Numerics;

namespace SomeEngine.Animation.Pose;

internal sealed class FootPlacementSamples
{
    internal readonly FootLandingPrediction[] Landings;
    internal readonly FootContactTarget[] SwingTargets;
    internal readonly Vector3[] PelvisCorrections;
    internal readonly float[] OrientationAmounts;
    internal readonly bool[] Active;
    internal long PoseTicks, GeometryTicks;
    private readonly FootPlacementSettings _settings;
    private readonly FootSupportSettings _support;
    private readonly FootSupportQueries _queries;
    private readonly Matrix4x4[] _models;
    private readonly FootSupportRequest[] _requests;
    private readonly FootContactTarget[] _surfaces;
    private readonly FootTerrainSample[] _envelope;
    private readonly Vector3[] _path;
    private readonly int[] _queryFeet;
    private readonly Vector3[] _departures, _departureOffsets;
    private readonly double[] _swingStarts;
    private readonly bool[] _swinging;
    private readonly FootHistory[] _previous, _transitionFrom;
    private Vector3 _previousPelvis, _pelvisVelocity, _transitionPelvis, _transitionPelvisVelocity;
    private double _previousTime, _transitionStart;
    private float _transitionDuration;
    private bool _initialized, _transition;

    internal FootPlacementSamples(in FootLockParameters parameters)
    {
        _settings = parameters.Prediction!;
        _support = parameters.Support;
        int feet = parameters.Rig.Legs.Length;
        Landings = new FootLandingPrediction[feet];
        SwingTargets = new FootContactTarget[feet];
        PelvisCorrections = new Vector3[feet];
        OrientationAmounts = new float[feet];
        Active = new bool[feet];
        _models = new Matrix4x4[feet * 3];
        _requests = new FootSupportRequest[feet];
        _surfaces = new FootContactTarget[feet];
        _queryFeet = new int[feet];
        _departures = new Vector3[feet];
        _departureOffsets = new Vector3[feet];
        _swingStarts = new double[feet];
        _swinging = new bool[feet];
        _previous = new FootHistory[feet];
        _transitionFrom = new FootHistory[feet];
        _envelope = new FootTerrainSample[_settings.EnvelopeSamples];
        _path = new Vector3[_envelope.Length];
        _queries = new(parameters.Scene, parameters.Support, Math.Max(feet, _envelope.Length));
    }

    internal void Reset()
    {
        Array.Clear(Landings);
        Array.Clear(_swinging);
        Array.Clear(Active);
        Array.Clear(_previous);
        _pelvisVelocity = Vector3.Zero;
        _initialized = _transition = false;
    }

    internal void ChangeSource()
    {
        Array.Clear(Landings);
        Array.Clear(_swinging);
        _transition = _initialized;
        _transitionStart = _previousTime;
        _transitionDuration = _settings.TransitionDuration;
        _previous.CopyTo(_transitionFrom, 0);
        _transitionPelvis = _previousPelvis;
        _transitionPelvisVelocity = _pelvisVelocity;
    }

    internal void Predict(double time, double calculationTime, FootPrediction prediction,
        ReadOnlySpan<FootPredictionSample> motion, ReadOnlySpan<Matrix4x4> source,
        in Matrix4x4 actor, in FootMovementInput movement)
    {
        PoseTicks = GeometryTicks = 0;
        Array.Clear(Active);
        int count = 0;
        long poseStart = Stopwatch.GetTimestamp();
        for (int foot = 0; foot < Active.Length; foot++)
        {
            if (motion[foot].Contact.Kind != FootContactKind.Free)
            { _swinging[foot] = false; continue; }
            if (!prediction.TryGetNextContact(foot, time, calculationTime, out FootPredictionSample contact))
            { Landings[foot] = default; continue; }
            Span<Matrix4x4> models = _models.AsSpan(foot * 3, 3);
            PosePredictionSample sample = prediction.SamplePose(foot, contact.Prediction.Time, calculationTime, models);
            Matrix4x4 futureActor = movement.Sample(sample.Time, time, prediction.Request, actor);
            Vector3 point = Vector3.Transform(models[contact.Contact.Kind == FootContactKind.ToePivot ? 2 : 1].Translation, futureActor);
            _requests[count] = new(point, contact.Contact.Kind, sample.Time);
            _queryFeet[count++] = foot;
            Landings[foot] = new(sample, models[0] * futureActor, default);
            if (!_swinging[foot])
            {
                Vector3 pivot = Pivot(foot, contact.Contact.Kind, source);
                if (_initialized)
                {
                    Matrix4x4.Decompose(source[1 + foot * 5 + 2], out _, out Quaternion sourceRotation, out Vector3 sourceTip);
                    Matrix4x4.Decompose(actor, out _, out Quaternion actorRotation, out _);
                    Vector3 local = Vector3.Transform(Vector3.TransformNormal(pivot - sourceTip, actor),
                        Quaternion.Conjugate(actorRotation * sourceRotation));
                    _departures[foot] = _previous[foot].Position + Vector3.Transform(local, _previous[foot].Orientation);
                    _swingStarts[foot] = Math.Max(prediction.Request.Origin.GetTime(motion[foot].Contact.Start), _previousTime);
                }
                else
                {
                    _departures[foot] = Vector3.Transform(pivot, actor);
                    _swingStarts[foot] = time;
                }
                _departureOffsets[foot] = _departures[foot] - Vector3.Transform(pivot, actor);
                _swinging[foot] = true;
            }
            if (_transition) _transitionDuration = Math.Min(_transitionDuration, (float)(sample.Time - _transitionStart));
        }
        PoseTicks = Stopwatch.GetTimestamp() - poseStart;
        if (count == 0) return;
        long queryStart = Stopwatch.GetTimestamp();
        _queries.Sample(_requests.AsSpan(0, count), _surfaces.AsSpan(0, count));
        GeometryTicks += Stopwatch.GetTimestamp() - queryStart;
        for (int query = 0; query < count; query++)
        {
            int foot = _queryFeet[query];
            FootContactTarget landing = _surfaces[query];
            Landings[foot] = Landings[foot] with { Contact = landing };
            if (!landing.HasSupport) continue;
            Active[foot] = true;
            float amount = Math.Clamp((float)((time - _swingStarts[foot]) / (landing.Time - _swingStarts[foot])), 0, 1);
            float blend = amount * amount * (3 - 2 * amount);
            Vector3 terrainCorrection = landing.Support.Point - _requests[query].Position;
            Vector3 point = Vector3.Transform(Pivot(foot, landing.Kind, source), actor)
                + (1 - blend) * _departureOffsets[foot] + blend * terrainCorrection;
            Vector3 start = _departures[foot], end = landing.Support.Point;
            poseStart = Stopwatch.GetTimestamp();
            for (int sample = 0; sample < _path.Length; sample++)
            {
                float fraction = (float)sample / (_path.Length - 1);
                double sampleTime = _swingStarts[foot] + (landing.Time - _swingStarts[foot]) * fraction;
                FootTrajectory trajectory = prediction.Sample(foot, sampleTime, calculationTime).Trajectory;
                Matrix4x4 predictedActor = movement.Sample(sampleTime, time, prediction.Request, actor);
                Vector3 pivot = landing.Kind == FootContactKind.ToePivot ? trajectory.Toe : trajectory.Heel;
                float adjustment = fraction * fraction * (3 - 2 * fraction);
                _path[sample] = Vector3.Transform(pivot, predictedActor)
                    + (1 - adjustment) * _departureOffsets[foot] + adjustment * terrainCorrection;
            }
            _path[0] = start;
            _path[^1] = end;
            PoseTicks += Stopwatch.GetTimestamp() - poseStart;
            queryStart = Stopwatch.GetTimestamp();
            _queries.SampleEnvelope(_path, _settings.FootRadius, _envelope);
            GeometryTicks += Stopwatch.GetTimestamp() - queryStart;
            float lift = _settings.FootRadius + _settings.Clearance;
            for (int sample = 1; sample + 1 < _envelope.Length; sample++)
            {
                FootTerrainSample terrain = _envelope[sample];
                if (!terrain.HasSupport) continue;
                float fraction = terrain.Fraction;
                float baseline = Vector3.Dot(Vector3.Lerp(start, end, fraction), _support.Up);
                float ground = Vector3.Dot(terrain.Center, _support.Up) - _settings.FootRadius;
                lift = Math.Max(lift, (ground - baseline) / (4 * fraction * (1 - fraction))
                    + _settings.FootRadius + _settings.Clearance);
            }
            float pathHeight = Vector3.Dot(Vector3.Lerp(start, end, amount), _support.Up) + lift * 4 * amount * (1 - amount);
            point += _support.Up * Math.Max(0, pathHeight - Vector3.Dot(point, _support.Up));
            SwingTargets[foot] = landing with { Support = landing.Support with { Point = point } };
            OrientationAmounts[foot] = blend;
            PelvisCorrections[foot] = terrainCorrection * blend;
        }
    }

    internal bool TryGetContact(int foot, double time, FootContactKind kind, out FootContactTarget contact)
    {
        contact = Landings[foot].Contact;
        return contact.HasSupport && contact.Kind == kind && contact.Time <= time;
    }

    internal void Continue(double time, in Matrix4x4 actor, in Matrix4x4 worldToActor,
        Vector3 sourcePelvis, Span<GpuPoseGoal> goals, ref Vector3 pelvisOffset, float weight)
    {
        float delta = (float)(time - _previousTime), elapsed = (float)(time - _transitionStart);
        float amount = _transition ? Math.Clamp(elapsed / _transitionDuration, 0, 1) : 1;
        float blend = amount * amount * (3 - 2 * amount);
        if (amount >= 1) _transition = false;
        Matrix4x4.Decompose(actor, out _, out Quaternion actorRotation, out _);
        for (int foot = 0; foot < goals.Length; foot++)
        {
            ref GpuPoseGoal goal = ref goals[foot];
            Vector3 position = Vector3.Transform(goal.Position, actor);
            Quaternion orientation = actorRotation * goal.Orientation;
            if (_transition)
            {
                FootHistory from = _transitionFrom[foot];
                position = Vector3.Lerp(from.Position + from.Velocity * elapsed, position, blend);
                Quaternion rotation = InertialPoseMath.ToQuaternion(from.AngularVelocity * elapsed) * from.Orientation;
                orientation = Quaternion.Slerp(rotation, orientation, blend);
                goal.Position = Vector3.Transform(position, worldToActor);
                goal.Orientation = Quaternion.Normalize(Quaternion.Conjugate(actorRotation) * orientation);
                goal.PositionWeight = goal.OrientationWeight = weight;
            }
            FootHistory previous = _previous[foot];
            _previous[foot] = new(position, orientation,
                _initialized && delta > 0 ? (position - previous.Position) / delta : previous.Velocity,
                _initialized && delta > 0 ? InertialPoseMath.ToRotationVector(orientation * Quaternion.Conjugate(previous.Orientation)) / delta : previous.AngularVelocity);
        }
        Vector3 pelvis = Vector3.Transform(sourcePelvis + pelvisOffset, actor);
        if (_transition)
        {
            pelvis = Vector3.Lerp(_transitionPelvis + _transitionPelvisVelocity * elapsed, pelvis, blend);
            pelvisOffset = Vector3.Transform(pelvis, worldToActor) - sourcePelvis;
        }
        if (_initialized && delta > 0) _pelvisVelocity = (pelvis - _previousPelvis) / delta;
        _previousPelvis = pelvis;
        _previousTime = time;
        _initialized = true;
    }

    private static Vector3 Pivot(int foot, FootContactKind kind, ReadOnlySpan<Matrix4x4> source) =>
        source[1 + foot * 5 + (kind == FootContactKind.ToePivot ? 4 : 3)].Translation;

    private readonly record struct FootHistory(Vector3 Position, Quaternion Orientation, Vector3 Velocity, Vector3 AngularVelocity);
}
