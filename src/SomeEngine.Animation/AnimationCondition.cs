using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public interface IAnimationCondition<TParameters, TState>
    where TParameters : struct
    where TState : struct
{
    static abstract bool Matches(in TParameters parameters, in GraphParameters inputs, ref TState state);
    static virtual void Initialize(in TParameters parameters, ref TState state) => state = default;
    static virtual void Release(ref TState state) => state = default;
}

public readonly struct GraphCondition<TParameters, TState, TCondition>
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    public GraphCondition(in TParameters parameters, QueryDescription? inputs = null) =>
        (Parameters, Inputs) = (parameters, inputs);

    public TParameters Parameters { get; }
    public QueryDescription? Inputs { get; }
}

public readonly partial struct GraphCondition
{
    public static GraphCondition<TParameters, TState, TCondition> Create<TParameters, TState, TCondition>(
        in TParameters parameters, QueryDescription? inputs = null)
        where TParameters : struct
        where TState : struct
        where TCondition : struct, IAnimationCondition<TParameters, TState> => new(in parameters, inputs);
}

public struct ParameterConditionState { }

public readonly struct ParameterCondition : IAnimationCondition<GraphCondition, ParameterConditionState>
{
    public static bool Matches(in GraphCondition parameters, in GraphParameters inputs, ref ParameterConditionState state) =>
        parameters.Matches(in inputs);
}

public ref partial struct StateMachineScope
{
    public unsafe void Transition<TParameters, TState, TCondition>(int from, int to,
        GraphCondition<TParameters, TState, TCondition> condition, float crossfade = 0)
        where TParameters : struct
        where TState : struct
        where TCondition : struct, IAnimationCondition<TParameters, TState>
    {
        DevelopmentCheck.Require(_data[0].Current == _scope.Id, "Declare a transition directly in its state machine.");
        DevelopmentCheck.Require(float.IsFinite(crossfade) && crossfade >= 0, "Crossfade duration must be finite and nonnegative.");
        GraphBuilding.Add(_data, new GraphNode
        {
            Kind = GraphNodeKind.Transition,
            StateId = from,
            NextState = to,
            FadeDuration = crossfade,
            ConditionData = new GraphConditionData<TParameters, TState, TCondition>(condition.Parameters, condition.Inputs),
            Matches = &GraphConditionEvaluation<TParameters, TState, TCondition>.Matches,
        });
    }
}

internal struct GraphConditionState<TParameters, TState, TCondition> : IBufferElement
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    internal TState Value;
}

internal abstract class GraphConditionData(QueryDescription? inputs)
{
    internal QueryDescription? Inputs { get; } = inputs;
    internal abstract Type Type { get; }
    internal abstract void Initialize(World world, Entity animation, int index);
    internal abstract void Release(World world, Entity animation, int index);
}

internal sealed class GraphConditionData<TParameters, TState, TCondition>(in TParameters parameters, QueryDescription? inputs) : GraphConditionData(inputs)
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    private readonly int _access = GraphConditionAccess<TParameters, TState, TCondition>.Id;
    private readonly int _inputs = GraphConditionAccesses.AddInputs(inputs);
    internal readonly TParameters Parameters = parameters;
    internal override Type Type => typeof(GraphConditionData<TParameters, TState, TCondition>);

    internal override void Initialize(World world, Entity animation, int index)
    {
        if (!world.Has<Buffer<GraphConditionState<TParameters, TState, TCondition>>>(animation))
            world.AddBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation);
        var initialization = (Index: index, Parameters: Parameters);
        {
            using var states = world.GetBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation);
            while (states.Count <= initialization.Index)
                states.Add(default);
            TCondition.Initialize(in initialization.Parameters, ref states[initialization.Index].Value);
        }
    }

    internal override void Release(World world, Entity animation, int index)
    {
        {
            using var states = world.GetBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation);
            TCondition.Release(ref states[index].Value);
        }
    }
}

internal static unsafe class GraphConditionAccess<TParameters, TState, TCondition>
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    internal static readonly int Id = GraphConditionAccesses.Add(&Declare);
    private static void Declare(QueryDescription query) =>
        query.Optional<Buffer<GraphConditionState<TParameters, TState, TCondition>>>();
}

internal static unsafe class GraphConditionAccesses
{
    private static readonly List<Entry> Entries = new();
    private static readonly List<QueryDescription> Inputs = new();
    private static readonly object Gate = new();
    internal static int Version { get; private set; }

    internal static int Add(delegate* managed<QueryDescription, void> declare)
    {
        lock (Gate)
        {
            Entries.Add(new Entry(declare));
            return ++Version;
        }
    }

    internal static void Declare(QueryDescription query)
    {
        lock (Gate)
        {
            foreach (Entry entry in Entries)
                entry.Declare(query);
            foreach (QueryDescription inputs in Inputs)
                query.WithAccess(inputs);
        }
    }

    internal static int AddInputs(QueryDescription? inputs)
    {
        lock (Gate)
        {
            if (inputs is null)
                return Version;
            foreach (QueryDescription previous in Inputs)
                if (ReferenceEquals(previous, inputs))
                    return Version;
            Inputs.Add(inputs);
            return ++Version;
        }
    }

    private readonly struct Entry(delegate* managed<QueryDescription, void> declare)
    {
        internal readonly delegate* managed<QueryDescription, void> Declare = declare;
    }
}

internal static class GraphConditionEvaluation<TParameters, TState, TCondition>
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    internal static bool Matches(World world, Entity animation, in GraphNode node, in GraphParameters inputs)
    {
        var evaluation = (Definition: (GraphConditionData<TParameters, TState, TCondition>)node.ConditionData!,
            Index: node.ConditionIndex, Inputs: inputs, Matches: false);
        {
            using var states = world.GetBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation);
            evaluation.Matches = TCondition.Matches(in evaluation.Definition.Parameters, in evaluation.Inputs,
                ref states[evaluation.Index].Value);
        }
        return evaluation.Matches;
    }
}
