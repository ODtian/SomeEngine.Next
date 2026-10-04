using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public sealed partial class HitTestSystem
{
    private QueryHandle _scrollQuery;
    private readonly Dictionary<Entity, ScrollInput> _scrollInputs = new();

    private void BeginScrollFrame(World world)
    {
        Collect(world, _scrollQuery, _nodes);
        _scrollInputs.Clear();
        foreach (Entity node in _nodes)
        {
            ScrollInput input = world.Read<ScrollInput>(node);
            input.Delta = Vector2.Zero;
            _scrollInputs.Add(node, input);
        }
    }

    private void ScrollWheel(World world, Entity hit, Vector2 delta)
    {
        for (Entity node = hit; node != Entity.Null; node = Hierarchy<ElementTree>.GetParent(world, node))
        {
            if (_scrollInputs.ContainsKey(node))
            {
                AddScroll(world, node, delta);
                return;
            }
            if (world.Has<Canvas>(node))
                return;
        }
    }

    private void AddScroll(World world, Entity node, Vector2 delta)
    {
        if (!_scrollInputs.TryGetValue(node, out ScrollInput input))
            return;
        ScrollOffset offset = world.Read<ScrollOffset>(node);
        Vector2 value = Vector2.Clamp(offset.Value + input.Delta + delta, Vector2.Zero, offset.Max);
        input.Delta = value - offset.Value;
        input.Count = unchecked(input.Count + 1);
        _scrollInputs[node] = input;
    }

    private void CommitScrollInputs(World world)
    {
        foreach (var (node, input) in _scrollInputs)
        {
            ScrollInput previous = world.Read<ScrollInput>(node);
            if (input.Delta != previous.Delta || input.Count != previous.Count)
                world.Replace(node, input);
        }
    }
}
