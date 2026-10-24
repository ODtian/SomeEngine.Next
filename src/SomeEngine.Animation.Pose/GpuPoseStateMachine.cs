namespace SomeEngine.Animation.Pose;

public enum GpuPoseComparison { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

public readonly record struct GpuPoseState(QuantizedClipData Clip, bool Loop = false, float Speed = 1);

public readonly record struct GpuPoseTransition(int Source, int Target, int Input,
    GpuPoseComparison Comparison, float Threshold, float Duration = 0);

/// <summary>Shared GPU states and ordered control comparisons, with independent playback for each installed leaf.</summary>
public sealed class GpuPoseStateMachine
{
    private readonly GpuPoseState[] _states;
    private readonly GpuPoseTransition[] _transitions;
    private readonly (int Start, int Count)[] _ranges;

    public ReadOnlySpan<GpuPoseState> States => _states;
    public ReadOnlySpan<GpuPoseTransition> Transitions => _transitions;
    public int InitialState { get; }
    public int InputCount { get; }

    public GpuPoseStateMachine(ReadOnlySpan<GpuPoseState> states, ReadOnlySpan<GpuPoseTransition> transitions,
        int initialState = 0)
    {
        _states = states.ToArray();
        _transitions = new GpuPoseTransition[transitions.Length];
        _ranges = new (int Start, int Count)[states.Length];
        InitialState = initialState;
        int count = 0;
        for (int state = 0; state < states.Length; state++)
        {
            int start = count;
            foreach (GpuPoseTransition transition in transitions)
                if (transition.Source == state)
                {
                    _transitions[count++] = transition;
                    InputCount = Math.Max(InputCount, transition.Input + 1);
                }
            _ranges[state] = (start, count - start);
        }
    }

    public ReadOnlySpan<GpuPoseTransition> TransitionsFrom(int state)
    {
        (int start, int count) = _ranges[state];
        return _transitions.AsSpan(start, count);
    }
}
