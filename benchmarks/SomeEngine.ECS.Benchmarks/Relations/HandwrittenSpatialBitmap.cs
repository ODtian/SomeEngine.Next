using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Independent direct bitmap proofs over the same collision nodes and summary fields.</summary>
public static class HandwrittenSpatialBitmap
{
    public static bool OneWay<TOutput>(World world, Query bodies, BoundsTree tree,
        ReadOnlySpan<BoundsBitmapData> categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().OpenRead();
        var execution = new OneWayPairs<TOutput>(tree, categories, output);
        try { execution.Execute(scope); return execution.Completed; }
        finally { output = execution.Output; }
    }

    private ref struct OneWayPairs<TOutput>(BoundsTree tree,
        ReadOnlySpan<BoundsBitmapData> categories, TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<BoundsBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(scoped QueryScope members)
        {
            Completed = Traverse(members);
        }

        private bool Traverse(scoped QueryScope members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
            Span<BitmapNodePair> work = stackalloc BitmapNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                BitmapNodePair pair = work[--count];
                ref readonly BoundsNode first = ref nodes[pair.First];
                ref readonly BoundsNode second = ref nodes[pair.Second];
                if (pair.First == pair.Second && first.IsLeaf ||
                    pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                    continue;
                GroupMatch forward = OneWayGroups(nodes, _categories, pair.First, pair.Second, pair.Forward);
                GroupMatch reverse = OneWayGroups(nodes, _categories, pair.Second, pair.First, pair.Reverse);
                if (forward == GroupMatch.None && reverse == GroupMatch.None)
                    continue;
                if (pair.First == pair.Second)
                {
                    work[count++] = new(first.SecondChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.FirstChild, forward, reverse);
                }
                else if (first.IsLeaf && second.IsLeaf)
                {
                    bool ascending = EntityOrder.Precedes(first.Entity, second.Entity);
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    if (match == GroupMatch.None)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.Contains(entities.Item1) || !members.Contains(entities.Item2))
                        continue;
                    if (match == GroupMatch.Unknown)
                    {
                        ref readonly var firstBits = ref _categories[firstNode];
                        ref readonly var secondBits = ref _categories[secondNode];
                        if (firstBits.ComponentCount == 0 || secondBits.ComponentCount == 0 ||
                            (firstBits.AllowedAny & secondBits.CategoriesAny) == 0)
                            continue;
                    }
                    if (!Output.Add(in entities))
                        return false;
                }
                else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
                {
                    work[count++] = new(first.SecondChild, pair.Second, forward, reverse);
                    work[count++] = new(first.FirstChild, pair.Second, forward, reverse);
                }
                else
                {
                    work[count++] = new(pair.First, second.SecondChild, forward, reverse);
                    work[count++] = new(pair.First, second.FirstChild, forward, reverse);
                }
            }
            return true;
        }
    }

    private static GroupMatch OneWayGroups(ReadOnlySpan<BoundsNode> nodes,
        ReadOnlySpan<BoundsBitmapData> categories, int firstNode, int secondNode, GroupMatch inherited)
    {
        if (inherited != GroupMatch.Unknown)
            return inherited;
        ref readonly var first = ref categories[firstNode];
        ref readonly var second = ref categories[secondNode];
        if ((first.AllowedAny & second.CategoriesAny) == 0)
            return GroupMatch.None;
        if (first.ComponentCount == nodes[firstNode].MemberCount &&
            second.ComponentCount == nodes[secondNode].MemberCount &&
            (first.AllowedCommon & second.CategoriesCommon) != 0)
            return GroupMatch.All;
        return GroupMatch.Unknown;
    }

    public static bool Mutual<TOutput>(World world, Query bodies, BoundsTree tree,
        ReadOnlySpan<BoundsBitmapData> categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().OpenRead();
        var execution = new MutualPairs<TOutput>(tree, categories, output);
        try { execution.Execute(scope); return execution.Completed; }
        finally { output = execution.Output; }
    }

    private ref struct MutualPairs<TOutput>(BoundsTree tree,
        ReadOnlySpan<BoundsBitmapData> categories, TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<BoundsBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(scoped QueryScope members)
        {
            Completed = Traverse(members);
        }

        private bool Traverse(scoped QueryScope members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<BoundsNode> nodes = tree.Nodes;
            Span<BitmapNodePair> work = stackalloc BitmapNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                BitmapNodePair pair = work[--count];
                ref readonly BoundsNode first = ref nodes[pair.First];
                ref readonly BoundsNode second = ref nodes[pair.Second];
                if (pair.First == pair.Second && first.IsLeaf ||
                    pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                    continue;
                GroupMatch forward = MutualGroups(nodes, _categories, pair.First, pair.Second, pair.Forward);
                GroupMatch reverse = MutualGroups(nodes, _categories, pair.Second, pair.First, pair.Reverse);
                if (forward == GroupMatch.None && reverse == GroupMatch.None)
                    continue;
                if (pair.First == pair.Second)
                {
                    work[count++] = new(first.SecondChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.SecondChild, forward, reverse);
                    work[count++] = new(first.FirstChild, first.FirstChild, forward, reverse);
                }
                else if (first.IsLeaf && second.IsLeaf)
                {
                    bool ascending = EntityOrder.Precedes(first.Entity, second.Entity);
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    if (match == GroupMatch.None)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.Contains(entities.Item1) || !members.Contains(entities.Item2))
                        continue;
                    if (match == GroupMatch.Unknown)
                    {
                        ref readonly var firstBits = ref _categories[firstNode];
                        ref readonly var secondBits = ref _categories[secondNode];
                        if (firstBits.ComponentCount == 0 || secondBits.ComponentCount == 0 ||
                            (firstBits.AllowedAny & secondBits.CategoriesAny) == 0 ||
                            (secondBits.AllowedAny & firstBits.CategoriesAny) == 0)
                            continue;
                    }
                    if (!Output.Add(in entities))
                        return false;
                }
                else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
                {
                    work[count++] = new(first.SecondChild, pair.Second, forward, reverse);
                    work[count++] = new(first.FirstChild, pair.Second, forward, reverse);
                }
                else
                {
                    work[count++] = new(pair.First, second.SecondChild, forward, reverse);
                    work[count++] = new(pair.First, second.FirstChild, forward, reverse);
                }
            }
            return true;
        }
    }

    private static GroupMatch MutualGroups(ReadOnlySpan<BoundsNode> nodes,
        ReadOnlySpan<BoundsBitmapData> categories, int firstNode, int secondNode, GroupMatch inherited)
    {
        if (inherited != GroupMatch.Unknown)
            return inherited;
        ref readonly var first = ref categories[firstNode];
        ref readonly var second = ref categories[secondNode];
        if ((first.AllowedAny & second.CategoriesAny) == 0 || (second.AllowedAny & first.CategoriesAny) == 0)
            return GroupMatch.None;
        if (first.ComponentCount == nodes[firstNode].MemberCount &&
            second.ComponentCount == nodes[secondNode].MemberCount &&
            (first.AllowedCommon & second.CategoriesCommon) != 0 &&
                (second.AllowedCommon & first.CategoriesCommon) != 0)
            return GroupMatch.All;
        return GroupMatch.Unknown;
    }

    private readonly struct BitmapNodePair(int first, int second, GroupMatch forward, GroupMatch reverse)
    {
        public readonly int First = first;
        public readonly int Second = second;
        public readonly GroupMatch Forward = forward;
        public readonly GroupMatch Reverse = reverse;
    }
}
