using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>The path time and speed corresponding to an explicit caller clock time.</summary>
public readonly record struct PoseTimeOrigin(double Time, float PathTime = 0, float Speed = 1)
{
    public float GetPathTime(double time) => (float)(PathTime + (time - Time) * Speed);
    public double GetTime(float pathTime) => Time + (pathTime - PathTime) / Speed;
}

/// <summary>A selected path, its time mapping and the caller's identifier for the selected playback conditions.</summary>
public readonly record struct PosePredictionRequest(PosePath Path, PoseTimeOrigin Origin, ulong Conditions = 0)
{
    public float GetPathTime(double time) =>
        Math.Clamp(Origin.GetPathTime(time), 0, Path.Duration);
}

/// <summary>The request, requested clock time, sampled path time and calculation clock time of one result.</summary>
public readonly record struct PosePredictionSample(PosePredictionRequest Request, double Time, float PathTime, double CalculatedAt);

/// <summary>One selected request and its output bones, preserving caller order.</summary>
public sealed class PosePredictionSelection
{
    public PosePredictionRequest Request { get; }
    public ReadOnlySpan<short> Bones => Selection.Bones;
    internal ClipSampleSelection Selection { get; }

    public PosePredictionSelection(PosePredictionRequest request, ReadOnlySpan<short> bones, ModelPose? parentPose = null)
    {
        Request = request;
        Selection = new(request.Path.Clip, bones, parentPose);
    }
}

/// <summary>Bound selected-path requests using the existing clip batch and reusable time storage.</summary>
public sealed class PosePredictionBatch
{
    private readonly PosePredictionSelection[] _selections;
    private readonly ClipSampleBatch _batch;
    private readonly float[] _times;

    public int Count => _selections.Length;
    public int OutputCount => _batch.OutputCount;

    public PosePredictionBatch(ReadOnlySpan<PosePredictionSelection> selections)
    {
        _selections = selections.ToArray();
        var clips = new ClipSampleSelection[selections.Length];
        for (int sample = 0; sample < selections.Length; sample++) clips[sample] = selections[sample].Selection;
        _batch = new(clips);
        _times = new float[selections.Length];
    }

    public PoseRange GetOutputRange(int sample) => _batch.GetOutputRange(sample);

    /// <summary>Samples the requested future clock times and returns selected model-space bones with their conditions.</summary>
    public void SampleModel(ReadOnlySpan<double> times, double calculationTime, Span<Matrix4x4> destination,
        Span<PosePredictionSample> samples)
    {
        for (int sample = 0; sample < _selections.Length; sample++)
        {
            PosePredictionRequest request = _selections[sample].Request;
            float pathTime = request.GetPathTime(times[sample]);
            _times[sample] = pathTime;
            samples[sample] = new(request, times[sample], pathTime, calculationTime);
        }
        _batch.SampleModel(_times, destination);
    }
}

/// <summary>A concrete pose algorithm that copies its initial mutable history and advances its own pose state.</summary>
public interface IPosePredictionAlgorithm<TParameters, TState>
    where TParameters : struct where TState : struct
{
    /// <summary>Copies mutable history while retaining shared immutable definitions and resources.</summary>
    static abstract TState CopyState(in TState initial);

    static abstract void Advance(in TParameters parameters, in MotionTime time,
        ReadOnlySpan<BoneTransform> target, Span<BoneTransform> pose, ref TState state);
}

/// <summary>A selected path with separately held pose and algorithm history, advanced in caller-selected steps.</summary>
public sealed class PosePrediction<TParameters, TState, TAlgorithm>
    where TParameters : struct where TState : struct
    where TAlgorithm : struct, IPosePredictionAlgorithm<TParameters, TState>
{
    private readonly TParameters _parameters;
    private TState _state;
    private readonly BoneTransform[] _target, _pose;
    private readonly ClipSampleBatch _source, _result;
    private double _time;

    public PosePredictionRequest Request { get; }
    public int OutputCount => _result.OutputCount;

    /// <summary>Starts with the caller's local pose and history at the request's time origin.</summary>
    public PosePrediction(PosePredictionSelection selection, in TParameters parameters, in TState initialState,
        ReadOnlySpan<BoneTransform> initialPose)
    {
        Request = selection.Request;
        _parameters = parameters;
        _state = TAlgorithm.CopyState(in initialState);
        _time = Request.Origin.Time;
        _pose = initialPose.ToArray();
        _target = new BoneTransform[Request.Path.Skeleton.BoneCount];
        _source = new([new ClipSampleSelection(Request.Path.Clip)]);
        var clip = new AnimationClip(Request.Path.Skeleton, 0, ClipSampler.Create<PoseData, PoseSampler>(new(_pose)));
        _result = new([new ClipSampleSelection(clip, selection.Bones, selection.Selection.ParentPose)]);
    }

    /// <summary>Advances from the previous clock time, then returns the selected model-space bones and request metadata.</summary>
    public PosePredictionSample AdvanceTo(double time, double calculationTime, Span<Matrix4x4> destination)
    {
        float pathTime = Request.GetPathTime(time);
        _source.SampleLocal([pathTime], _target);
        var step = new MotionTime(Request.Origin.GetPathTime(time), (float)((time - _time) * Request.Origin.Speed));
        TAlgorithm.Advance(in _parameters, in step, _target, _pose, ref _state);
        _time = time;
        _result.SampleModel([0], destination);
        return new(Request, time, pathTime, calculationTime);
    }

    private sealed record PoseData(BoneTransform[] Values);

    private readonly struct PoseSampler : IClipSampler<PoseData>
    {
        public static void SampleLocal(PoseData data, float time, Span<BoneTransform> destination) =>
            data.Values.CopyTo(destination);
    }
}
