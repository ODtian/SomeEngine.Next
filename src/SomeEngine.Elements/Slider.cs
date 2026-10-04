using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements;

/// <summary>A horizontal range control composed with the shared value behavior.</summary>
public record struct Slider(float Value, float Minimum = 0, float Maximum = 1, float Step = 0) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Scope.Entity.Accessible(Role.Slider, "");
        var range = new ValueRange(Value, Minimum, Maximum, Step);
        ValueFunctions.ValueDraggable(Scope.Entity, in range, Vector2.UnitX, 148);
        bool focused = Scope.Entity.Focused();
        using (Absolute().Size(160, 24).Content())
        {
            Absolute().Size(160, 4).Position(0, 10).Background(new Color(0.3f, 0.3f, 0.3f)).Corner(2);
            Absolute().Size(6 + 148 * range.Ratio, 4).Position(0, 10).Background(new Color(0.15f, 0.45f, 0.85f)).Corner(2);
            Absolute().Size(12, 12).Position(148 * range.Ratio, 6).Background(new Color(1, 1, 1)).Corner(6)
                .Border(new Color(0.7f, 0.8f, 1), focused ? 2 : 0);
        }
        return default;
    }
}
