using SomeEngine.Job;

namespace SomeEngine.Navigation;

public enum NavigationFlowStatus : byte { Searching, Ready }

public class NavigationFlowBuildState(NavigationFlowInput input)
{
    public NavigationFlowInput Input { get; } = input;
    public NavigationFlowStatus Status { get; protected internal set; }
    public NavigationFlowField? Field { get; protected internal set; }
}

public interface INavigationFlowSolver<TState> where TState : NavigationFlowBuildState
{
    static abstract string Id { get; }
    static abstract TState Create(NavigationFlowInput input);
    static abstract JobHandle Schedule(Scheduler scheduler, TState state, int budget, JobHandle dependency);
}

/// <summary>Keeps one shared field's solver workspace across budgeted calls and installs completed inputs.</summary>
public sealed class NavigationFlowSystem<TSolver, TState>(NavigationFlow flow, Scheduler? scheduler = null)
    where TSolver : struct, INavigationFlowSolver<TState>
    where TState : NavigationFlowBuildState
{
    private readonly Scheduler _scheduler = scheduler ?? JobSystem.Default;
    private JobHandle _completion;
    public NavigationFlow Flow { get; } = flow;
    public TState State { get; private set; } = TSolver.Create(flow.Input);

    public NavigationBuild<TState> Schedule(int budget = int.MaxValue, JobHandle dependency = default)
    {
        NavigationFlowInput input = Flow.Input;
        if (!ReferenceEquals(State.Input, input))
            State = TSolver.Create(input);
        JobHandle ready = _scheduler.CombineDependencies([_completion, dependency]);
        JobHandle solved = TSolver.Schedule(_scheduler, State, budget, ready);
        _completion = _scheduler.Schedule(new ApplyField(Flow, State),
            [JobResourceAccess.Read(_scheduler.GetResourceToken(State)),
             JobResourceAccess.Write(_scheduler.GetResourceToken(Flow))], solved);
        return new(State, _completion);
    }

    private readonly struct ApplyField(NavigationFlow flow, TState state) : IJob
    {
        public void Execute()
        {
            if (state.Status == NavigationFlowStatus.Ready)
                flow.Apply(state.Field!);
        }
    }
}
