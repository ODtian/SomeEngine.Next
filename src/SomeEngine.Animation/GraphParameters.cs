using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;

namespace SomeEngine.Animation;

/// <summary>Inputs written by the application that drives an animation graph.</summary>
public struct GraphParameters : IComponent
{
    public ParameterArray Values;
}

[InlineArray(16)]
public struct ParameterArray
{
    private float _value;
}

public enum ParameterComparison : byte { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

public readonly struct GraphCondition
{
    public readonly byte Parameter;
    public readonly ParameterComparison Comparison;
    public readonly float Value;

    public GraphCondition(byte parameter, ParameterComparison comparison, float value)
    {
        DevelopmentCheck.Require(parameter < 16, "Graph parameter index must fit ParameterArray.");
        DevelopmentCheck.Require(comparison <= ParameterComparison.GreaterOrEqual, "Graph condition requires a defined comparison.");
        (Parameter, Comparison, Value) = (parameter, comparison, value);
    }

    public static GraphCondition Equal(byte parameter, float value) => new(parameter, ParameterComparison.Equal, value);

    public bool Matches(in GraphParameters parameters)
    {
        float value = parameters.Values[Parameter];
        return Comparison switch
        {
            ParameterComparison.Equal => value == Value,
            ParameterComparison.NotEqual => value != Value,
            ParameterComparison.Less => value < Value,
            ParameterComparison.LessOrEqual => value <= Value,
            ParameterComparison.Greater => value > Value,
            _ => value >= Value,
        };
    }
}

public ref struct StateMachineScope
{
    private readonly BuildData[] _data;
    private BuilderScope _scope;

    internal StateMachineScope(BuildData[] data)
    {
        _data = data;
        _scope = GraphBuilding.Open(data, GraphNodeKind.StateMachine);
    }

    public BuilderScope State(int id, bool initial = false)
    {
        DevelopmentCheck.Require(_data[0].Current == _scope.Id, "Declare a state directly in its state machine.");
        BuilderScope state = GraphBuilding.Open(_data, GraphNodeKind.State);
        _data[0].Nodes![state.Id].StateId = id;
        _data[0].Nodes![state.Id].Initial = initial;
        return state;
    }

    public void Transition(int from, int to, GraphCondition condition, float crossfade = 0)
    {
        DevelopmentCheck.Require(_data[0].Current == _scope.Id, "Declare a transition directly in its state machine.");
        DevelopmentCheck.Require(float.IsFinite(crossfade) && crossfade >= 0, "Crossfade duration must be finite and nonnegative.");
        GraphBuilding.Add(_data, new GraphNode
        {
            Kind = GraphNodeKind.Transition, StateId = from, NextState = to, Condition = condition, FadeDuration = crossfade,
        });
    }

    public void Dispose() => _scope.Dispose();
}

public ref struct Blend1DScope
{
    private readonly BuildData[] _data;
    private BuilderScope _scope;

    internal Blend1DScope(BuildData[] data, byte parameter)
    {
        DevelopmentCheck.Require(parameter < 16, "Blend coordinate index must fit ParameterArray.");
        _data = data;
        _scope = GraphBuilding.Open(data, GraphNodeKind.Blend1D);
        data[0].Nodes![_scope.Id].Parameter = parameter;
    }

    public BuilderScope Point(float coordinate)
    {
        DevelopmentCheck.Require(float.IsFinite(coordinate), "Blend sample coordinate must be finite.");
        DevelopmentCheck.Require(_data[0].Current == _scope.Id, "Declare a blend sample directly in its blend scope.");
        return GraphBuilding.Open(_data, new GraphNode { Kind = GraphNodeKind.BlendPoint, Coordinate = coordinate });
    }

    public void Dispose() => _scope.Dispose();
}
