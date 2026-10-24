using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A foot trajectory and contact interval on the selected request's cumulative path clock.</summary>
public readonly record struct FootPredictionSample(PosePredictionSample Prediction,
    FootContactSegment Contact, FootTrajectory Trajectory);

/// <summary>A bounded future window that reuses imported foot paths and batches composed-path sampling once.</summary>
public sealed class FootPrediction
{
    private readonly float _pathStart;
    private readonly FootContactSegment[][] _contacts;
    private readonly FootMotion? _trajectory;
    private readonly PosePredictionBatch[] _poses;
    private readonly PosePredictionSample[] _poseSample = new PosePredictionSample[1];
    public PosePredictionRequest Request { get; }
    public double StartTime => Request.Origin.Time;
    public double EndTime { get; }
    public int FootCount => _contacts.Length;

    public FootPrediction(PosePredictionRequest request, float lookAhead, ModelPose? parentPose = null)
    {
        Request = request;
        FootMotion definition = request.Path.FindFootMotion()!;
        FootAnalysisSettings settings = definition.Settings;
        _pathStart = Math.Max(0, request.Origin.PathTime - 1 / settings.SampleRate);
        float pathEnd = request.GetPathTime(request.Origin.Time + lookAhead);
        EndTime = request.Origin.GetTime(pathEnd);
        _contacts = new FootContactSegment[definition.FootCount][];
        _poses = new PosePredictionBatch[definition.FootCount];
        var feet = new FootBones[definition.FootCount];
        var bones = new short[1 + definition.FootCount * 2];
        bones[0] = definition.RootBone;
        for (int foot = 0; foot < feet.Length; foot++)
        {
            feet[foot] = definition.Bones(foot);
            bones[1 + foot * 2] = feet[foot].Heel;
            bones[2 + foot * 2] = feet[foot].Toe;
            _poses[foot] = new([new(request, [definition.RootBone, feet[foot].Heel, feet[foot].Toe], parentPose)]);
        }

        var times = new List<float> { _pathStart, pathEnd };
        request.Path.CollectFootTimes(_pathStart, pathEnd, 0, times);
        if (!request.Path.HasFootTrajectory)
        {
            int count = (int)MathF.Ceiling((pathEnd - _pathStart) * settings.SampleRate);
            for (int sample = 1; sample < count; sample++) times.Add(_pathStart + (pathEnd - _pathStart) * sample / count);
        }
        float[] pathTimes = times.Distinct().Order().ToArray();
        if (request.Path.HasFootTrajectory)
        {
            for (int foot = 0; foot < feet.Length; foot++)
            {
                var contacts = new List<FootContactSegment>();
                for (int sample = 0; sample < pathTimes.Length - 1; sample++)
                {
                    float start = pathTimes[sample], end = pathTimes[sample + 1];
                    FootContactKind kind = request.Path.FootIntent(foot, (start + end) * 0.5f, automatic: true)!.Value;
                    AddContact(contacts, new(start - _pathStart, end - _pathStart, kind));
                }
                _contacts[foot] = contacts.ToArray();
            }
            return;
        }

        var selections = new PosePredictionSelection[pathTimes.Length];
        var clocks = new double[pathTimes.Length];
        var relativeTimes = new float[pathTimes.Length];
        for (int sample = 0; sample < pathTimes.Length; sample++)
        {
            selections[sample] = new(request, bones, parentPose);
            clocks[sample] = request.Origin.GetTime(pathTimes[sample]);
            relativeTimes[sample] = pathTimes[sample] - _pathStart;
        }
        var batch = new PosePredictionBatch(selections);
        var transforms = new Matrix4x4[batch.OutputCount];
        var metadata = new PosePredictionSample[batch.Count];
        batch.SampleModel(clocks, request.Origin.Time, transforms, metadata);
        var markers = new List<FootContactMarker>();
        for (int foot = 0; foot < feet.Length; foot++)
            for (int sample = 0; sample < pathTimes.Length - 1; sample++)
                if (request.Path.FootIntent(foot, (pathTimes[sample] + pathTimes[sample + 1]) * 0.5f, automatic: false) is { } kind)
                    markers.Add(new(foot, relativeTimes[sample], relativeTimes[sample + 1], kind));
        _trajectory = FootMotion.FromSamples(definition.RootBone, feet, in settings, relativeTimes, transforms, markers.ToArray());
        for (int foot = 0; foot < feet.Length; foot++) _contacts[foot] = _trajectory.Feet[foot].Contacts;
    }

    /// <summary>Returns the trajectory and current contact kind within this prediction window.</summary>
    public FootPredictionSample Sample(int foot, double time, double calculationTime)
    {
        float pathTime = Request.GetPathTime(time);
        ReadOnlySpan<FootContactSegment> contacts = _contacts[foot];
        int index = 0;
        while (index + 1 < contacts.Length && contacts[index + 1].Start + _pathStart <= pathTime) index++;
        return Sample(foot, time, pathTime, calculationTime, contacts[index]);
    }

    /// <summary>Finds the next transition from free motion into contact within the bound window.</summary>
    public bool TryGetNextContact(int foot, double afterTime, double calculationTime, out FootPredictionSample sample)
    {
        float pathTime = Request.GetPathTime(afterTime);
        ReadOnlySpan<FootContactSegment> contacts = _contacts[foot];
        for (int index = 0; index < contacts.Length; index++)
        {
            FootContactSegment contact = contacts[index];
            float start = contact.Start + _pathStart;
            if (contact.Kind == FootContactKind.Free || start < pathTime ||
                index > 0 && contacts[index - 1].Kind != FootContactKind.Free) continue;
            sample = Sample(foot, Request.Origin.GetTime(start), start, calculationTime, contact);
            return true;
        }
        sample = default;
        return false;
    }

    /// <summary>Samples the selected root, heel and toe model matrices through the common pose batch.</summary>
    public PosePredictionSample SamplePose(int foot, double time, double calculationTime, Span<Matrix4x4> destination)
    {
        _poses[foot].SampleModel([time], calculationTime, destination, _poseSample);
        return _poseSample[0];
    }

    private FootPredictionSample Sample(int foot, double time, float pathTime, double calculationTime, FootContactSegment contact)
    {
        FootTrajectory trajectory = _trajectory is null
            ? Request.Path.SampleFoot(foot, pathTime) : _trajectory.Sample(foot, pathTime - _pathStart);
        return new(new(Request, time, pathTime, calculationTime),
            new(contact.Start + _pathStart, contact.End + _pathStart, contact.Kind), trajectory);
    }

    private static void AddContact(List<FootContactSegment> contacts, FootContactSegment contact)
    {
        if (contacts.Count > 0 && contacts[^1].Kind == contact.Kind) contacts[^1] = contacts[^1] with { End = contact.End };
        else contacts.Add(contact);
    }
}
