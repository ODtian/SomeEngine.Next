using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

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
    public GraphCondition(in TParameters parameters, QueryDefinition? inputs = null) =>
        (Parameters, Inputs) = (parameters, inputs ?? QueryDefinition.Empty);

    public TParameters Parameters { get; }
    public QueryDefinition Inputs { get; }
}

public readonly partial struct GraphCondition
{
    public static GraphCondition<TParameters, TState, TCondition> Create<TParameters, TState, TCondition>(
        in TParameters parameters, QueryDefinition? inputs = null)
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
            Kind = GraphNodeKind.Transition, StateId = from, NextState = to, FadeDuration = crossfade,
            ConditionData = new GraphConditionData<TParameters, TState, TCondition>(condition.Parameters, condition.Inputs),
            Matches = &GraphConditionEvaluation<TParameters, TState, TCondition>.Matches,
        });
    }
}

[BufferCapacity(1)]
internal struct GraphConditionState<TParameters, TState, TCondition> : IBufferElement
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    internal TState Value;
}

internal abstract class GraphConditionData(QueryDefinition inputs)
{
    internal QueryDefinition Inputs { get; } = inputs;
    internal abstract Type Type { get; }
    internal abstract void Initialize(World world, Entity animation, int index);
    internal abstract void Release(World world, Entity animation, int index);
}

internal sealed class GraphConditionData<TParameters, TState, TCondition>(in TParameters parameters, QueryDefinition inputs) : GraphConditionData(inputs)
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
        if (!world.HasBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation))
            world.AddBuffer<GraphConditionState<TParameters, TState, TCondition>>(animation);
        var initialization = (Index: index, Parameters: Parameters);
        world.ExecuteBufferWrite<GraphConditionState<TParameters, TState, TCondition>, (int Index, TParameters Parameters)>(
            animation, ref initialization, static (states, ref initialization) =>
            {
                while (states.Count <= initialization.Index)
                    states.Add(default);
                TCondition.Initialize(in initialization.Parameters, ref states[initialization.Index].Value);
            });
    }

    internal override void Release(World world, Entity animation, int index)
    {
        world.ExecuteBufferWrite<GraphConditionState<TParameters, TState, TCondition>, int>(animation, ref index,
            static (states, ref index) => TCondition.Release(ref states[index].Value));
    }
}

internal static unsafe class GraphConditionAccess<TParameters, TState, TCondition>
    where TParameters : struct
    where TState : struct
    where TCondition : struct, IAnimationCondition<TParameters, TState>
{
    internal static readonly int Id = GraphConditionAccesses.Add(&Declare);
    private static void Declare(QueryDefinitionBuilder query) =>
        query.OptionalBuffer<GraphConditionState<TParameters, TState, TCondition>>(QueryAccess.ReadWrite);
}

internal static unsafe class GraphConditionAccesses
{
    private static readonly List<Entry> Entries = new();
    private static readonly List<QueryDefinition> Inputs = new();
    private static readonly object Gate = new();
    internal static int Version { get; private set; }

    internal static int Add(delegate* managed<QueryDefinitionBuilder, void> declare)
    {
        lock (Gate)
        {
            Entries.Add(new Entry(declare));
            return ++Version;
        }
    }

    internal static void Declare(QueryDefinitionBuilder query)
    {
        lock (Gate)
        {
            foreach (Entry entry in Entries)
                entry.Declare(query);
            foreach (QueryDefinition inputs in Inputs)
                query.WithAccess(inputs);
        }
    }

    internal static int AddInputs(QueryDefinition inputs)
    {
        lock (Gate)
        {
            if (inputs == QueryDefinition.Empty)
                return Version;
            foreach (QueryDefinition previous in Inputs)
                if (previous.Key.Equals(inputs.Key))
                    return Version;
            Inputs.Add(inputs);
            return ++Version;
        }
    }

    private readonly struct Entry(delegate* managed<QueryDefinitionBuilder, void> declare)
    {
        internal readonly delegate* managed<QueryDefinitionBuilder, void> Declare = declare;
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
        world.ExecuteBufferWrite<GraphConditionState<TParameters, TState, TCondition>,
            (GraphConditionData<TParameters, TState, TCondition> Definition, int Index, GraphParameters Inputs, bool Matches)>(
            animation, ref evaluation, static (states, ref evaluation) =>
            {
                evaluation.Matches = TCondition.Matches(in evaluation.Definition.Parameters, in evaluation.Inputs,
                    ref states[evaluation.Index].Value);
            });
        return evaluation.Matches;
    }
}
