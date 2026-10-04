using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class ValueFunctions
{
    /// <summary>Consumes a new pointer or focused confirmation of a toggle.</summary>
    public static bool Toggled(this Entity toggle) => toggle.Clicked();

    /// <summary>Declares shared range editing with a caller-chosen drag direction and distance.</summary>
    public static Entity ValueDraggable(Entity node, in ValueRange range, Vector2 direction, float distance)
    {
        DevelopmentCheck.Require(float.IsFinite(range.Minimum) && float.IsFinite(range.Maximum) &&
            range.Minimum <= range.Maximum, "Value editing requires finite, increasing limits.");
        DevelopmentCheck.Require(float.IsFinite(range.Value) && range.Value >= range.Minimum &&
            range.Value <= range.Maximum, "Value editing requires a finite value within its limits.");
        DevelopmentCheck.Require(float.IsFinite(range.Step) && range.Step >= 0,
            "Value editing requires a finite, nonnegative step.");
        DevelopmentCheck.Require(float.IsFinite(direction.X) && float.IsFinite(direction.Y) &&
            direction.LengthSquared() > 0 && float.IsFinite(direction.LengthSquared()) &&
            float.IsFinite(distance) && distance > 0, "Value dragging requires a finite direction and positive distance.");
        DraggableFunctions.Draggable(node);
        FocusFunctions.Focusable(node);
        Scope.Write<ValueRange>(node) = range;
        Scope.Write<ValueDrag>(node) = new ValueDrag(Vector2.Normalize(direction), distance);
        Scope.Ensure<ValueEdit>(node);
        Scope.Ensure<ValueEditSeen>(node);
        Scope.Ensure<ValueDragStart>(node);
        return node;
    }

    /// <summary>Consumes the latest changed input value; the caller decides how to save it.</summary>
    public static bool Edited(this Entity node, out float value)
    {
        ValueEdit edit = Scope.Read<ValueEdit>(node);
        ref ValueEditSeen seen = ref Scope.Write<ValueEditSeen>(node);
        bool edited = edit.Count != seen.Count;
        seen.Count = edit.Count;
        value = edited ? edit.Value : Scope.Read<ValueRange>(node).Value;
        return edited;
    }
}
