using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Independent direct bitmap proofs over the same collision nodes and summary fields.</summary>
public static class HandwrittenSpatialBitmap
{
    public static bool OneWay<TOutput>(World world, QueryHandle bodies, CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition definition = world.GetQueryDefinition(bodies);
        QueryHandle members = world.Query(definition);
        QueryHandle access = world.Query(QueryDefinition.Empty.WithAccessFrom(definition).WithOptionalRead<Body>());
        var execution = new OneWayAccess<TOutput>(world, members, tree, categories, output);
        try
        {
            world.ExecuteQuery(access, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(access);
            world.ReleaseQuery(members);
        }
    }

    private ref struct OneWayAccess<TOutput>(World world, QueryHandle members, CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor access)
        {
            var execution = new OneWayPairs<TOutput>(tree, _categories, Output);
            try
            {
                world.ExecuteQuery(members, ref execution);
                Completed = execution.Completed;
            }
            finally
            {
                Output = execution.Output;
            }
        }
    }

    private ref struct OneWayPairs<TOutput>(CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor members)
        {
            Completed = Traverse(members);
        }

        private bool Traverse(QueryCursor members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
            Span<BitmapNodePair> work = stackalloc BitmapNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                BitmapNodePair pair = work[--count];
                ref readonly CollisionNode first = ref nodes[pair.First];
                ref readonly CollisionNode second = ref nodes[pair.Second];
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
                    bool ascending = EntityOrder.Compare(first.Entity, second.Entity) < 0;
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    if (match == GroupMatch.None)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.TryGetRow(entities.Item1, out _) || !members.TryGetRow(entities.Item2, out _))
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

    private static GroupMatch OneWayGroups(ReadOnlySpan<CollisionNode> nodes,
        ReadOnlySpan<CollisionBitmapData> categories, int firstNode, int secondNode, GroupMatch inherited)
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

    public static bool Mutual<TOutput>(World world, QueryHandle bodies, CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition definition = world.GetQueryDefinition(bodies);
        QueryHandle members = world.Query(definition);
        QueryHandle access = world.Query(QueryDefinition.Empty.WithAccessFrom(definition).WithOptionalRead<Body>());
        var execution = new MutualAccess<TOutput>(world, members, tree, categories, output);
        try
        {
            world.ExecuteQuery(access, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            world.ReleaseQuery(access);
            world.ReleaseQuery(members);
        }
    }

    private ref struct MutualAccess<TOutput>(World world, QueryHandle members, CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor access)
        {
            var execution = new MutualPairs<TOutput>(tree, _categories, Output);
            try
            {
                world.ExecuteQuery(members, ref execution);
                Completed = execution.Completed;
            }
            finally
            {
                Output = execution.Output;
            }
        }
    }

    private ref struct MutualPairs<TOutput>(CollisionTree tree,
        ReadOnlySpan<CollisionBitmapData> categories, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionBitmapData> _categories = categories;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor members)
        {
            Completed = Traverse(members);
        }

        private bool Traverse(QueryCursor members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
            Span<BitmapNodePair> work = stackalloc BitmapNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                BitmapNodePair pair = work[--count];
                ref readonly CollisionNode first = ref nodes[pair.First];
                ref readonly CollisionNode second = ref nodes[pair.Second];
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
                    bool ascending = EntityOrder.Compare(first.Entity, second.Entity) < 0;
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    if (match == GroupMatch.None)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.TryGetRow(entities.Item1, out _) || !members.TryGetRow(entities.Item2, out _))
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

    private static GroupMatch MutualGroups(ReadOnlySpan<CollisionNode> nodes,
        ReadOnlySpan<CollisionBitmapData> categories, int firstNode, int secondNode, GroupMatch inherited)
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
