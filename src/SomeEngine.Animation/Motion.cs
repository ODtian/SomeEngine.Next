using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;

namespace SomeEngine.Animation;

public readonly record struct MotionBoundary(StageEnd End, float Duration = 0)
{
    public TimelineStage Stage => new() { End = End, Duration = Duration };
}

public readonly struct MotionValues<T>(T from, T to, T current) where T : unmanaged
{
    public readonly T From = from;
    public readonly T To = to;
    public readonly T Current = current;
}

public readonly record struct MotionTime(float Time, float Delta);

public readonly record struct MotionSample<T>(T Value, bool Finished) where T : unmanaged;

public interface IEasing
{
    static abstract float Sample(float progress);
}

/// <summary>An algorithm's parameters, history, endpoints, and completion lifecycle.</summary>
public interface IMotion<T, TOps, TParameters, TState>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
{
    static abstract MotionBoundary Describe(in TParameters parameters);
    static abstract void Initialize(in TParameters parameters, in MotionValues<T> values, ref TState state);
    static abstract MotionSample<T> Evaluate(in TParameters parameters, in MotionTime time,
        in MotionValues<T> values, ref TState state);
    static abstract void Retarget(in TParameters previous, in TParameters next,
        in MotionValues<T> values, ref TState state);

    static virtual void UpdateParameters(in TParameters previous, in TParameters next, ref TState state) { }

    static virtual bool ParametersEqual(in TParameters previous, in TParameters next) =>
        EqualityComparer<TParameters>.Default.Equals(previous, next);

    static virtual void Release(ref TState state) => state = default;

    static virtual void EvaluateBatch<TMotion>(ref MotionBatch<T, TParameters, TState> batch)
        where TMotion : struct, IMotion<T, TOps, TParameters, TState>
    {
        for (int index = 0; index < batch.Count; index++)
        {
            if (!batch.IsActive(index))
                continue;
            ref readonly TParameters parameters = ref batch.Parameters(index);
            ref TState state = ref batch.State(index);
            MotionTime time = batch.Time(index);
            MotionValues<T> values = batch.Values(index);
            MotionSample<T> sample = TMotion.Evaluate(in parameters, in time, in values, ref state);
            batch.SetSample(index, in sample);
        }
    }
}

/// <summary>Typed instance storage; parameter and state references remain visible to the GC.</summary>
[BufferCapacity(1)]
public struct MotionData<T, TParameters, TState> : IBufferElement
    where T : unmanaged
    where TParameters : struct
    where TState : struct
{
    public TParameters Parameters;
    public TState State;
    public MotionValues<T> Values;
    public MotionTime Time;
    public MotionSample<T> Sample;
    public bool Active;
}

/// <summary>A bounded view of the actual typed instance storage used by a batch.</summary>
public ref struct MotionBatch<T, TParameters, TState>
    where T : unmanaged
    where TParameters : struct
    where TState : struct
{
    private readonly Span<MotionData<T, TParameters, TState>> _data;
    private readonly ReadOnlySpan<TParameters> _parameters;
    private readonly Span<TState> _states;
    private readonly ReadOnlySpan<MotionValues<T>> _values;
    private readonly ReadOnlySpan<MotionTime> _times;
    private readonly Span<MotionSample<T>> _samples;

    public MotionBatch(Span<MotionData<T, TParameters, TState>> data)
    {
        _data = data;
        Count = data.Length;
    }

    /// <summary>Single-element parameter and time spans provide shared inputs.</summary>
    public MotionBatch(ReadOnlySpan<TParameters> parameters, Span<TState> states,
        ReadOnlySpan<MotionValues<T>> values, ReadOnlySpan<MotionTime> times,
        Span<MotionSample<T>> samples)
    {
        _parameters = parameters;
        _states = states;
        _values = values;
        _times = times;
        _samples = samples;
        Count = states.Length;
    }

    public int Count { get; }
    public Span<MotionData<T, TParameters, TState>> Data => _data;
    public ReadOnlySpan<TParameters> ParameterSpan => _parameters;
    public Span<TState> StateSpan => _states;
    public ReadOnlySpan<MotionValues<T>> ValueSpan => _values;
    public ReadOnlySpan<MotionTime> TimeSpan => _times;
    public Span<MotionSample<T>> SampleSpan => _samples;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsActive(int index) => _data.IsEmpty || _data[index].Active;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly TParameters Parameters(int index)
    {
        if (!_data.IsEmpty)
            return ref _data[index].Parameters;
        return ref _parameters[_parameters.Length == 1 ? 0 : index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref TState State(int index)
    {
        if (!_data.IsEmpty)
            return ref _data[index].State;
        return ref _states[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MotionValues<T> Values(int index) => _data.IsEmpty ? _values[index] : _data[index].Values;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MotionTime Time(int index) => _data.IsEmpty ? _times[_times.Length == 1 ? 0 : index] : _data[index].Time;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSample(int index, in MotionSample<T> sample)
    {
        if (!_data.IsEmpty)
            _data[index].Sample = sample;
        else
            _samples[index] = sample;
    }
}

/// <summary>The shared managed entry address invokes one concrete loop per batch.</summary>
public static unsafe class MotionDispatcher<T, TOps, TParameters, TState, TEvaluator>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
    where TParameters : struct
    where TState : struct
    where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
{
    public static readonly delegate* managed<ref MotionBatch<T, TParameters, TState>, void> Function = &Evaluate;

    public static void EvaluateBatch(ref MotionBatch<T, TParameters, TState> batch) => Function(ref batch);

    private static void Evaluate(ref MotionBatch<T, TParameters, TState> batch) =>
        TEvaluator.EvaluateBatch<TEvaluator>(ref batch);
}
