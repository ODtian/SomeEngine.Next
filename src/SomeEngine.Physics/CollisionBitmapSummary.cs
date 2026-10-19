using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using System.Runtime.CompilerServices;

namespace SomeEngine.Physics;

public sealed partial class CollisionTree
{
    public CollisionBitmapSummary<TRelation, TImplementation, TComponent, TPermission>
        CreateBitmapSummary<TRelation, TImplementation, TComponent, TPermission>(TRelation relation, TImplementation implementation)
        where TRelation : struct, IRelation<TComponent, TComponent>
        where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
        where TComponent : struct
        where TPermission : struct, ICategoryPermission
        => new(this, relation, implementation);
}

public readonly struct CollisionBitmapData(int componentCount, ulong categoriesAny, ulong categoriesCommon,
    ulong allowedAny, ulong allowedCommon)
{
    public readonly int ComponentCount = componentCount;
    public readonly ulong CategoriesAny = categoriesAny;
    public readonly ulong CategoriesCommon = categoriesCommon;
    public readonly ulong AllowedAny = allowedAny;
    public readonly ulong AllowedCommon = allowedCommon;
}

public sealed class CollisionBitmapSummary<TRelation, TImplementation, TComponent, TPermission>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    private readonly TRelation _relation;
    private readonly TImplementation _implementation;
    private CollisionBitmapData[] _nodes = [];

    internal CollisionBitmapSummary(CollisionTree tree, TRelation relation, TImplementation implementation)
    {
        Tree = tree;
        _relation = relation;
        _implementation = implementation;
    }

    public CollisionTree Tree { get; }

    public ReadOnlySpan<CollisionBitmapData> Nodes => _nodes.AsSpan(0, Tree.NodeCount);

    public int NodeCapacity => _nodes.Length;

    public ref readonly CollisionBitmapData this[int node]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _nodes[node];
    }

    public void Rebuild(QueryCursor access)
    {
        Resize();
        if (Tree.Root >= 0)
            Fill(Tree.Root, access);
    }

    private void Fill(int node, QueryCursor access)
    {
        ref readonly CollisionNode value = ref Tree.Nodes[node];
        if (!value.IsLeaf)
        {
            Fill(value.FirstChild, access);
            Fill(value.SecondChild, access);
        }
        Refit(node, access);
    }

    public void Refit(int node, QueryCursor access)
    {
        Resize();
        ref readonly CollisionNode value = ref Tree.Nodes[node];
        if (value.IsLeaf)
        {
            if (access.TryGetRow(value.Entity, out var row) && row.Has<TComponent>())
            {
                ref readonly TComponent component = ref row.ReadRef<TComponent>();
                var bits = new CategoryBits(_implementation.ReadCategories(in _relation, in component),
                    _implementation.ReadAllowedCategories(in _relation, in component));
                _nodes[node] = new CollisionBitmapData(1, bits.Categories, bits.Categories,
                    bits.AllowedCategories, bits.AllowedCategories);
            }
            else
                _nodes[node] = default;
            return;
        }

        ref readonly var first = ref _nodes[value.FirstChild];
        ref readonly var second = ref _nodes[value.SecondChild];
        _nodes[node] = first.ComponentCount == 0 ? second : second.ComponentCount == 0 ? first :
            new CollisionBitmapData(first.ComponentCount + second.ComponentCount,
                first.CategoriesAny | second.CategoriesAny, first.CategoriesCommon & second.CategoriesCommon,
                first.AllowedAny | second.AllowedAny, first.AllowedCommon & second.AllowedCommon);
    }

    private void Resize()
    {
        if (_nodes.Length < Tree.NodeCount)
            Array.Resize(ref _nodes, Tree.NodeCount);
    }
}
