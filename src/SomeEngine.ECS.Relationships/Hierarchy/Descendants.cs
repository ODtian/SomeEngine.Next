using System.Buffers;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public readonly struct DescendantEntities<TDomain> where TDomain : struct, IHierarchyDomain
{
    private readonly World _world;
    private readonly Entity _root;
    internal DescendantEntities(World world, Entity root) => (_world, _root) = (world, root);
    public DescendantEnumerator<TDomain> GetEnumerator() => new(_world, _root);
}

internal struct DescendantFrame
{
    internal Entity[]? Items;
    internal int Count, Next;
}

public ref struct DescendantEnumerator<TDomain> where TDomain : struct, IHierarchyDomain
{
    private readonly World _world;
    private readonly HierarchyData<TDomain>? _data;
    private DescendantFrame[]? _frames;
    private int _depth;
    public Entity Current { get; private set; }

    internal DescendantEnumerator(World world, Entity root)
    {
        _world = world; _data = world.FindLifecycle<HierarchyData<TDomain>>();
        _frames = null; _depth = 0; Current = default;
        world.BeginStructureRead();
        if (_data is { } data) Push(data.ReadChildren(root));
    }

    private void Push(Children<TDomain> children)
    {
        if (children.Count == 0) return;
        if (_frames is null) _frames = ArrayPool<DescendantFrame>.Shared.Rent(8);
        else if (_depth == _frames.Length)
        {
            var frames = ArrayPool<DescendantFrame>.Shared.Rent(_depth * 2);
            _frames.AsSpan(0, _depth).CopyTo(frames);
            ArrayPool<DescendantFrame>.Shared.Return(_frames, clearArray: true);
            _frames = frames;
        }
        _frames[_depth++] = new() { Items = children.Items, Count = children.Count };
    }

    public bool MoveNext()
    {
        while (_depth > 0)
        {
            ref DescendantFrame frame = ref _frames![_depth - 1];
            if (frame.Next == frame.Count) { _frames[--_depth] = default; continue; }
            Entity child = frame.Items![frame.Next++];
            Push(_data!.ReadChildren(child));
            Current = child;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_frames is { } frames) ArrayPool<DescendantFrame>.Shared.Return(frames, clearArray: true);
        _world.EndValueRead();
    }
}
