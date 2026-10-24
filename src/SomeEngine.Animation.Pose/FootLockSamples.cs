using System.Diagnostics;
using System.Numerics;

namespace SomeEngine.Animation.Pose;

internal sealed class FootLockSamples
{
    internal readonly FootLockParameters Parameters;
    internal readonly FootContactTarget[] Contacts;
    internal readonly GpuPoseGoal[] Goals;
    internal Vector3 PelvisOffset;
    internal readonly FootPlacementSamples? Future;
    internal FootPlacementCosts Costs;
    private readonly SkeletonInstance _skeleton;
    private PosePredictionBatch _poses = null!;
    private PosePredictionRequest _source;
    private readonly Matrix4x4[] _models;
    private readonly FootPredictionSample[] _motion;
    private readonly Vector3[] _slidingOffsets;
    // The terrain correction is retained in support-local coordinates, before source foot rotation.
    private readonly Quaternion[] _contactRotations;
    private readonly FootSupportQueries _queries;
    private readonly FootSupportRequest[] _requests;
    private readonly FootContactTarget[] _targets;
    private readonly int[] _queryFeet;
    private readonly double[] _times = new double[1];
    private readonly PosePredictionSample[] _metadata = new PosePredictionSample[1];
    private FootPrediction _prediction = null!;

    internal FootLockSamples(in FootLockParameters parameters, SkeletonInstance skeleton)
    {
        Parameters = parameters;
        _skeleton = skeleton;
        Contacts = new FootContactTarget[parameters.Rig.Legs.Length];
        Goals = new GpuPoseGoal[parameters.Rig.Legs.Length];
        _models = new Matrix4x4[1 + Goals.Length * 5];
        _motion = new FootPredictionSample[Goals.Length];
        _slidingOffsets = new Vector3[Goals.Length];
        _contactRotations = new Quaternion[Goals.Length];
        _requests = new FootSupportRequest[Goals.Length];
        _targets = new FootContactTarget[Goals.Length];
        _queryFeet = new int[Goals.Length];
        _queries = new(parameters.Scene, parameters.Support, Goals.Length);
        Future = parameters.Prediction is null ? null : new(parameters);
        Bind(parameters.Source);
    }

    private void Bind(in PosePredictionRequest source)
    {
        _source = source;
        _prediction = new(source, Parameters.ContactWindow, _skeleton.ParentInstance?.ModelPose);
        FootMotion motion = source.Path.FindFootMotion()!;
        var bones = new short[_models.Length];
        bones[0] = (short)Parameters.Rig.PelvisBone;
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            FootLockLeg leg = Parameters.Rig.Legs[foot];
            FootBones points = motion.Bones(foot);
            int start = 1 + foot * 5;
            bones[start] = (short)leg.Chain.Root;
            bones[start + 1] = (short)leg.Chain.Middle;
            bones[start + 2] = (short)leg.Chain.Tip;
            bones[start + 3] = points.Heel;
            bones[start + 4] = points.Toe;
        }
        _poses = new([new(source, bones, _skeleton.ParentInstance?.ModelPose)]);
    }

    internal void Reset()
    {
        Array.Clear(Contacts);
        Array.Clear(_slidingOffsets);
        Array.Clear(_contactRotations);
        _prediction = new(_source, Parameters.ContactWindow, _skeleton.ParentInstance?.ModelPose);
        Future?.Reset();
    }

    internal void Update(float time, double calculationTime, in Matrix4x4 actor, float weight,
        in PosePredictionRequest request, in FootMovementInput movement)
    {
        long poseStart = Stopwatch.GetTimestamp(), geometryTicks = 0;
        if (request != _source) { Future?.ChangeSource(); Bind(request); }
        _times[0] = time;
        _poses.SampleModel(_times, calculationTime, _models, _metadata);
        if (time > _prediction.EndTime && _prediction.Request.GetPathTime(_prediction.EndTime) < _source.Path.Duration)
        {
            _prediction = new(new(_source.Path, new(time, _source.GetPathTime(time), _source.Origin.Speed), _source.Conditions),
                Parameters.ContactWindow, _skeleton.ParentInstance?.ModelPose);
        }
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            int start = 1 + foot * 5;
            Matrix4x4.Decompose(_models[start + 2], out _, out Quaternion rotation, out Vector3 position);
            Goals[foot] = new() { Position = position, Orientation = rotation,
                BendDirection = Vector3.Normalize(Vector3.TransformNormal(Parameters.Rig.Legs[foot].BendDirection, _models[start])) };
            _motion[foot] = _prediction.Sample(foot, time, calculationTime);
        }
        long poseTicks = Stopwatch.GetTimestamp() - poseStart;
        Future?.Predict(time, calculationTime, _prediction, _motion, _models, in actor, in movement);
        Matrix4x4.Invert(actor, out Matrix4x4 worldToActor);
        int count = 0;
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            FootContactKind kind = _motion[foot].Contact.Kind;
            FootContactTarget before = Contacts[foot];
            if (kind == FootContactKind.Free) { Contacts[foot] = new(time, kind, false, default, default, default); continue; }
            if (before.Kind == FootContactKind.Free && Future is { } future &&
                future.TryGetContact(foot, time, kind, out FootContactTarget landing) &&
                Refresh(in landing, out FootContactTarget landed, ref geometryTicks))
            {
                Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(landed.Support.Normal, Matrix4x4.Transpose(actor)));
                Quaternion rotation = ReadRotation(actor) * FabrikMath.FromTo(Parameters.Rig.NeutralSoles[foot], normal);
                _contactRotations[foot] = Quaternion.Normalize(Quaternion.Inverse(ReadRotation(landed.Support.Transform)) * rotation);
                Contacts[foot] = landed;
                continue;
            }
            Vector3 point = Vector3.Transform(Pivot(foot, kind), actor);
            if (Refresh(in before, out FootContactTarget current, ref geometryTicks))
            {
                Contacts[foot] = current;
                if (kind == before.Kind && kind != FootContactKind.Sliding)
                    continue;
                if (before.Kind == FootContactKind.Sliding)
                    point += Vector3.TransformNormal(_slidingOffsets[foot], current.Support.Transform);
                else
                {
                    GpuPoseGoal corrected = ContactGoal(foot, in Goals[foot], before.Kind, in current, in actor, in worldToActor);
                    point = Vector3.Transform(ContactPoint(foot, kind, in Goals[foot], in corrected), actor);
                }
            }
            else Contacts[foot] = default;
            _queryFeet[count] = foot;
            _requests[count++] = new(point, kind, time);
        }
        if (count > 0)
        {
            long queryStart = Stopwatch.GetTimestamp();
            _queries.Sample(_requests.AsSpan(0, count), _targets.AsSpan(0, count));
            geometryTicks += Stopwatch.GetTimestamp() - queryStart;
        }
        for (int query = 0; query < count; query++)
        {
            int foot = _queryFeet[query];
            FootContactTarget target = _targets[query];
            if (target.HasSupport)
            {
                FootContactTarget previous = Contacts[foot];
                Quaternion rotation;
                if (previous.HasSupport)
                {
                    Quaternion normalChange = FabrikMath.FromTo(previous.Support.Normal, target.Support.Normal);
                    rotation = normalChange * ReadRotation(previous.Support.Transform) * _contactRotations[foot];
                }
                else
                {
                    Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(target.Support.Normal, Matrix4x4.Transpose(actor)));
                    rotation = ReadRotation(actor) * FabrikMath.FromTo(Parameters.Rig.NeutralSoles[foot], normal);
                }
                _contactRotations[foot] = Quaternion.Normalize(Quaternion.Inverse(ReadRotation(target.Support.Transform)) * rotation);
            }
            Contacts[foot] = target;
            if (target.Kind == FootContactKind.Sliding && target.HasSupport)
            {
                Matrix4x4.Invert(target.Support.Transform, out Matrix4x4 inverse);
                _slidingOffsets[foot] = target.LocalPoint - Vector3.Transform(Vector3.Transform(Pivot(foot, target.Kind), actor), inverse);
            }
        }
        float preferred = 0;
        int constraints = 0;
        Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Parameters.Support.Up, worldToActor));
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            FootContactTarget contact = Contacts[foot];
            GpuPoseGoal source = Goals[foot];
            if (contact.HasSupport)
            {
                Goals[foot] = ContactGoal(foot, in source, contact.Kind, in contact, in actor, in worldToActor);
                preferred += Vector3.Dot(Goals[foot].Position - source.Position, up);
            }
            else if (Future is { } future && future.Active[foot])
            {
                FootContactTarget swing = future.SwingTargets[foot];
                Goals[foot] = ContactGoal(foot, in source, swing.Kind, in swing, in actor, in worldToActor, future.OrientationAmounts[foot]);
                preferred += Vector3.Dot(Vector3.TransformNormal(future.PelvisCorrections[foot], worldToActor), up);
            }
            else continue;
            constraints++;
        }
        PelvisOffset = Vector3.Zero;
        if (constraints > 0) CompensatePelvis(preferred / constraints, up, weight);
        Future?.Continue(time, in actor, in worldToActor, _models[0].Translation, Goals, ref PelvisOffset, weight);
        Costs = new(TimeSpan.FromSeconds((poseTicks + (Future?.PoseTicks ?? 0)) / (double)Stopwatch.Frequency),
            TimeSpan.FromSeconds((geometryTicks + (Future?.GeometryTicks ?? 0)) / (double)Stopwatch.Frequency));
    }

    private void CompensatePelvis(float preferred, Vector3 up, float weight)
    {
        float pelvisHeight = Vector3.Dot(_models[0].Translation, up);
        float minimum = float.NegativeInfinity, maximum = float.PositiveInfinity;
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            int start = 1 + foot * 5;
            float upper = Vector3.Distance(_models[start].Translation, _models[start + 1].Translation);
            float lower = Vector3.Distance(_models[start + 1].Translation, _models[start + 2].Translation);
            GpuTwoBoneChain chain = Parameters.Rig.Legs[foot].Chain;
            float squaredSum = upper * upper + lower * lower, twiceProduct = 2 * upper * lower;
            float nearSquared = squaredSum + twiceProduct * MathF.Cos(chain.MaximumBend);
            float farSquared = squaredSum + twiceProduct * MathF.Cos(chain.MinimumBend);
            Vector3 difference = _models[start].Translation - Goals[foot].Position;
            float height = Vector3.Dot(difference, up);
            float horizontalSquared = MathF.Max(0, difference.LengthSquared() - height * height);
            float near = MathF.Sqrt(MathF.Max(0, nearSquared - horizontalSquared));
            float far = MathF.Sqrt(MathF.Max(0, farSquared - horizontalSquared));
            minimum = Math.Max(minimum, pelvisHeight - height + (height >= 0 ? near : -far));
            maximum = Math.Min(maximum, pelvisHeight - height + (height >= 0 ? far : -near));
        }
        float finalHeight = Math.Clamp(pelvisHeight + preferred, minimum, maximum);
        PelvisOffset = up * ((finalHeight - pelvisHeight) * weight);
        for (int foot = 0; foot < Goals.Length; foot++)
        {
            Goals[foot].PositionWeight = weight;
            Goals[foot].OrientationWeight = weight;
        }
    }

    private bool Refresh(in FootContactTarget target, out FootContactTarget current, ref long ticks)
    {
        long start = Stopwatch.GetTimestamp();
        bool found = _queries.TryRefresh(in target, out current);
        ticks += Stopwatch.GetTimestamp() - start;
        return found;
    }

    private Vector3 Pivot(int foot, FootContactKind kind) =>
        _models[1 + foot * 5 + (kind == FootContactKind.ToePivot ? 4 : 3)].Translation;

    private GpuPoseGoal ContactGoal(int foot, in GpuPoseGoal source, FootContactKind kind,
        in FootContactTarget target, in Matrix4x4 actor, in Matrix4x4 worldToActor, float orientation = 1)
    {
        Quaternion rotation;
        if (Contacts[foot].HasSupport)
            rotation = Quaternion.Normalize(Quaternion.Inverse(ReadRotation(actor))
                * ReadRotation(target.Support.Transform) * _contactRotations[foot]);
        else
        {
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(target.Support.Normal, Matrix4x4.Transpose(actor)));
            rotation = Quaternion.Slerp(Quaternion.Identity, FabrikMath.FromTo(Parameters.Rig.NeutralSoles[foot], normal), orientation);
        }
        GpuPoseGoal goal = source;
        goal.Position = Vector3.Transform(target.Support.Point, worldToActor)
            - Vector3.Transform(Pivot(foot, kind) - source.Position, rotation);
        goal.Orientation = Quaternion.Normalize(rotation * source.Orientation);
        return goal;
    }

    private static Quaternion ReadRotation(in Matrix4x4 transform)
    {
        Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _);
        return rotation;
    }

    private Vector3 ContactPoint(int foot, FootContactKind kind, in GpuPoseGoal source, in GpuPoseGoal corrected) =>
        corrected.Position + Vector3.Transform(Pivot(foot, kind) - source.Position,
            corrected.Orientation * Quaternion.Conjugate(source.Orientation));
}
