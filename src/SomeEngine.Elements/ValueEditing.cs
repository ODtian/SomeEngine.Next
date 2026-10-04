using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements;

internal static class ValueEditing
{
    internal static void Drag(World world, Entity node, in Drag drag)
    {
        if (!world.Has<ValueRange>(node))
            return;
        ValueRange range = world.Read<ValueRange>(node);
        ValueDragStart start = world.Read<ValueDragStart>(node);
        if (start.Count != drag.Count)
        {
            start.Count = drag.Count;
            start.Value = Current(world, node, in range);
            world.Replace(node, start);
        }
        ValueDrag mapping = world.Read<ValueDrag>(node);
        double offset = Vector2.Dot(drag.Total, mapping.Direction) / mapping.Distance;
        Change(world, node, in range, start.Value + offset * ((double)range.Maximum - range.Minimum));
    }

    internal static bool Step(World world, Entity node, Key key)
    {
        int direction = key switch
        {
            Key.Right or Key.Up => 1,
            Key.Left or Key.Down => -1,
            _ => 0,
        };
        if (direction == 0 || node == Entity.Null || !world.Has<ValueRange>(node))
            return false;
        ValueRange range = world.Read<ValueRange>(node);
        double step = range.Step > 0 ? range.Step : ((double)range.Maximum - range.Minimum) / 20;
        Change(world, node, in range, Current(world, node, in range) + direction * step);
        return true;
    }

    private static float Current(World world, Entity node, in ValueRange range)
    {
        ValueEdit edit = world.Read<ValueEdit>(node);
        return edit.Count != world.Read<ValueEditSeen>(node).Count ? edit.Value : range.Value;
    }

    private static void Change(World world, Entity node, in ValueRange range, double candidate)
    {
        candidate = Math.Clamp(candidate, range.Minimum, range.Maximum);
        if (range.Step > 0 && candidate > range.Minimum && candidate < range.Maximum)
            candidate = range.Minimum + Math.Round((candidate - range.Minimum) / range.Step,
                MidpointRounding.AwayFromZero) * range.Step;
        float value = (float)Math.Clamp(candidate, range.Minimum, range.Maximum);
        if (value == Current(world, node, in range))
            return;
        ValueEdit edit = world.Read<ValueEdit>(node);
        edit.Value = value;
        edit.Count = unchecked(edit.Count + 1);
        world.Replace(node, edit);
    }
}

/// <summary>The pointer stage's initial value for the current drag count.</summary>
internal struct ValueDragStart : IComponent
{
    public float Value;
    public uint Count;
}
