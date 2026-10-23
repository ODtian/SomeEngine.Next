using SomeEngine.Job;

namespace SomeEngine.Navigation;

public enum NavigationFlowStatus : byte { Searching, Ready }

public class NavigationFlowBuildState(NavigationFlowInput input)
{
    public NavigationFlowInput Input { get; protected internal set; } = input;
    public NavigationFlowStatus Status { get; protected internal set; }
    public NavigationFlowField? Field { get; protected internal set; }
}

public interface INavigationFlowSolver<TState> where TState : NavigationFlowBuildState
{
    static abstract string Id { get; }
    static abstract TState Create(NavigationFlowInput input);
    /// <summary>Returns a retained workspace; null selects the solver's normal Create path.</summary>
    static virtual TState? Update(TState state, NavigationFlowInput input) => null;
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
        JobHandle ready = _scheduler.CombineDependencies([_completion, dependency]);
        var result = new SolveResult(State);
        _completion = _scheduler.Schedule(new Begin(this, result, budget),
            [JobResourceAccess.Read(_scheduler.GetResourceToken(Flow)), JobResourceAccess.Exclusive(_scheduler.GetResourceToken(this))], ready);
        return new(() => result.State, _completion);
    }

    private sealed class SolveResult(TState state) { public TState State = state; }
    private readonly struct Begin(NavigationFlowSystem<TSolver, TState> system, SolveResult result, int budget) : IJob
    {
        public void Execute()
        {
            NavigationFlowInput input = system.Flow.Input;
            if (!ReferenceEquals(system.State.Input, input)) system.State = TSolver.Update(system.State, input) ?? TSolver.Create(input);
            result.State = system.State;
            JobHandle solved = TSolver.Schedule(system._scheduler, system.State, budget, default);
            system._scheduler.Schedule(new ApplyField(system.Flow, system.State),
                [JobResourceAccess.Read(system._scheduler.GetResourceToken(system.State)),
                 JobResourceAccess.Write(system._scheduler.GetResourceToken(system.Flow))], solved);
        }
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
