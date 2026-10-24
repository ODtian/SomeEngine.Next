using SomeEngine.Animation;

namespace SomeEngine.Animation.Consumers;

public readonly struct SquareEasing : IEasing
{
    public static float Sample(float progress) => progress * progress;
}

public readonly record struct HistoryParameters(float Duration, float Gain, string Name);

public struct HistoryState
{
    public string? Name;
    public int Samples;
    public int Updates;
    public int Retargets;
}

public readonly struct HistoryMotion : IMotion<float, FloatOps, HistoryParameters, HistoryState>
{
    public static MotionBoundary Describe(in HistoryParameters parameters) => new(StageEnd.Duration, parameters.Duration);

    public static void Initialize(in HistoryParameters parameters, in MotionValues<float> values, ref HistoryState state) =>
        state = new HistoryState { Name = parameters.Name };

    public static void UpdateParameters(in HistoryParameters previous, in HistoryParameters next, ref HistoryState state)
    {
        state.Name = next.Name;
        state.Updates++;
    }

    public static void Retarget(in HistoryParameters previous, in HistoryParameters next,
        in MotionValues<float> values, ref HistoryState state) => state.Retargets++;

    public static MotionSample<float> Evaluate(in HistoryParameters parameters, in MotionTime time,
        in MotionValues<float> values, ref HistoryState state)
    {
        state.Samples++;
        bool finished = time.Time >= parameters.Duration;
        float value = finished ? values.To : values.From + (values.To - values.From) * time.Time / parameters.Duration * parameters.Gain;
        return new(value, finished);
    }

    public static bool ParametersEqual(in HistoryParameters previous, in HistoryParameters next) =>
        previous.Duration == next.Duration && previous.Gain == next.Gain && previous.Name == next.Name;

    public static void Release(ref HistoryState state) => state = default;
}

/// <summary>A consumer provides its own whole-batch entry through the same public interface.</summary>
public readonly struct HistoryBatchMotion : IMotion<float, FloatOps, HistoryParameters, HistoryState>
{
    public static MotionBoundary Describe(in HistoryParameters parameters) => HistoryMotion.Describe(in parameters);
    public static void Initialize(in HistoryParameters parameters, in MotionValues<float> values, ref HistoryState state) =>
        HistoryMotion.Initialize(in parameters, in values, ref state);
    public static void Retarget(in HistoryParameters previous, in HistoryParameters next,
        in MotionValues<float> values, ref HistoryState state) => HistoryMotion.Retarget(in previous, in next, in values, ref state);

    public static MotionSample<float> Evaluate(in HistoryParameters parameters, in MotionTime time,
        in MotionValues<float> values, ref HistoryState state) => HistoryMotion.Evaluate(in parameters, in time, in values, ref state);

    public static void EvaluateBatch<TMotion>(ref MotionBatch<float, HistoryParameters, HistoryState> batch)
        where TMotion : struct, IMotion<float, FloatOps, HistoryParameters, HistoryState>
    {
        for (int index = 0; index < batch.Count; index++)
        {
            if (!batch.IsActive(index))
                continue;
            ref readonly HistoryParameters parameters = ref batch.Parameters(index);
            ref HistoryState state = ref batch.State(index);
            MotionTime time = batch.Time(index);
            MotionValues<float> values = batch.Values(index);
            state.Samples++;
            bool finished = time.Time >= parameters.Duration;
            float value = finished ? values.To : values.From + (values.To - values.From) * time.Time / parameters.Duration * parameters.Gain;
            var sample = new MotionSample<float>(value, finished);
            batch.SetSample(index, in sample);
        }
    }
}

/// <summary>The same mass-one integration formula implemented in the consumer assembly.</summary>
public readonly struct ConsumerSpring : IMotion<float, FloatOps, SpringParameters, SpringState<float>>
{
    public static MotionBoundary Describe(in SpringParameters parameters) => new(StageEnd.Settled);
    public static void Initialize(in SpringParameters parameters, in MotionValues<float> values, ref SpringState<float> state) => state = default;
    public static void Retarget(in SpringParameters previous, in SpringParameters next,
        in MotionValues<float> values, ref SpringState<float> state) { }

    public static MotionSample<float> Evaluate(in SpringParameters parameters, in MotionTime time,
        in MotionValues<float> values, ref SpringState<float> state)
    {
        float current = values.Current;
        float remaining = time.Delta;
        while (remaining > 0)
        {
            float step = MathF.Min(remaining, parameters.Step);
            float displacement = values.To - current;
            float force = displacement * parameters.Stiffness;
            float resistance = state.Velocity * -parameters.Damping;
            float acceleration = force + resistance;
            state.Velocity += acceleration * step;
            current += state.Velocity * step;
            remaining -= step;
        }
        bool finished = MathF.Abs(current - values.To) < parameters.Tolerance && MathF.Abs(state.Velocity) < parameters.Tolerance;
        if (finished)
        {
            current = values.To;
            state.Velocity = 0;
        }
        return new(current, finished);
    }
}

public readonly record struct Distance(float Value);

public readonly struct DistanceOps : IValueOps<Distance>
{
    public static Distance Lerp(in Distance a, in Distance b, float progress) => new(a.Value + (b.Value - a.Value) * progress);
    public static Distance Add(in Distance a, in Distance b) => new(a.Value + b.Value);
    public static Distance Subtract(in Distance a, in Distance b) => new(a.Value - b.Value);
    public static Distance Scale(in Distance value, float scale) => new(value.Value * scale);
    public static float Distance(in Distance a, in Distance b) => MathF.Abs(a.Value - b.Value);
    public static bool Equals(in Distance a, in Distance b) => a.Value == b.Value;
}
