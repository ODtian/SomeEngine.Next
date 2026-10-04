using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests.Rewrites;

/// <summary>A user control with a round surface, rotating indicator, and upward value dragging.</summary>
public record struct Knob(float Value, float Minimum = 0, float Maximum = 1, float Step = 0) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        var range = new ValueRange(Value, Minimum, Maximum, Step);
        ValueFunctions.ValueDraggable(Scope.Entity, in range, -Vector2.UnitY, 148);
        float angle = (135 + 270 * range.Ratio) * MathF.PI / 180;
        using (Absolute().Size(48, 48).Background(new Color(0.1f, 0.2f, 0.25f)).Corner(24)
            .Border(new Color(1, 0.7f, 0.2f), Scope.Entity.Focused() ? 2 : 0).Content())
            Absolute().Size(6, 6).Position(21 + 14 * MathF.Cos(angle), 21 + 14 * MathF.Sin(angle))
                .Background(new Color(1, 0.7f, 0.2f)).Corner(3);
        return default;
    }
}
