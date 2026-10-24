using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views.SwiftUI;

/// <summary>A reference to a State that can be passed to another view.</summary>
public readonly struct Binding<T>
{
    private readonly State<T> _state;

    public Binding(State<T> state)
    {
        DevelopmentCheck.Require(state is not null, "Binding requires a State.");
        _state = state;
    }

    /// <summary>Reads or writes the shared State, recording reads in the current computation.</summary>
    public T Value
    {
        get
        {
            DevelopmentCheck.Require(_state is not null, "Construct a Binding from a State before reading its value.");
            return _state.Value;
        }
        set
        {
            DevelopmentCheck.Require(_state is not null, "Construct a Binding from a State before writing its value.");
            _state.Value = value;
        }
    }
}
