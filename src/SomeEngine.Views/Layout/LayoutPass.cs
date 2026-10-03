using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Layout;

/// <summary>The active node's measurement, placement, and component reads.</summary>
public ref struct LayoutPass
{
    private readonly LayoutData _data;
    private readonly int _nodeIndex;
    private readonly Vector2 _contentOrigin;
    public Entity Node { get; }
    public LayoutChildren Children { get; }

    internal LayoutPass(LayoutData data, Entity node, int nodeIndex, LayoutChildren children, Vector2 contentOrigin)
    {
        _data = data;
        _nodeIndex = nodeIndex;
        Node = node;
        Children = children;
        _contentOrigin = contentOrigin;
    }

    public Size Measure(Entity child, in Constraints constraints)
    {
        DevelopmentCheck.Require(Children.Contains(child), "Measure requires a child from this layout's Children.");
        LayoutValidation.CheckConstraints(in constraints);
        return _data.Measure(child, in constraints, _nodeIndex);
    }

    /// <summary>Places a measured child relative to this node's content origin.</summary>
    public void Place(Entity child, Vector2 position)
    {
        LayoutValidation.CheckPosition(position);
        _data.Place(child, position + _contentOrigin, _nodeIndex);
    }

    /// <summary>Reads table or sparse data; consume the reference before measuring a child.</summary>
    public ref readonly T Read<T>(Entity entity) where T : struct
    {
        DevelopmentCheck.Require(default(T) is IComponent or ISparseComponent,
            $"LayoutPass.Read requires table or sparse component {typeof(T).Name}.");
        return ref _data.World.ReadComponentReference<T>(entity);
    }

    public bool Has<T>(Entity entity) where T : struct => _data.World.HasComponentData<T>(entity);
}

/// <summary>The ordered layout children after transparent views have been expanded.</summary>
public readonly struct LayoutChildren
{
    private readonly List<Entity> _entities;
    internal LayoutChildren(List<Entity> entities) => _entities = entities;
    public int Count => _entities.Count;
    public Entity this[int index] => _entities[index];
    public List<Entity>.Enumerator GetEnumerator() => _entities.GetEnumerator();
    internal bool Contains(Entity entity) => _entities.Contains(entity);
}
