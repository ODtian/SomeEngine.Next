using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using GroupResults = System.ValueTuple<System.ValueTuple<SomeEngine.ECS.Spatial.GroupMatch, SomeEngine.ECS.Spatial.GroupMatch>, SomeEngine.ECS.Spatial.GroupMatch>;
using CategorySummary = SomeEngine.ECS.Spatial.BoundsBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public static partial class HandwrittenSpatialCollision
{
    public static bool EnumeratePartition<TOutput>(BoundsNodePair partition, scoped RelationQueryViews queries,
        BoundsTree tree, BoundsKeySummary<Hair, Entity> keys, CategorySummary categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var execution = new FilteredPairs<TOutput>(queries.World, tree, keys, categories, output, partition);
        try
        {
            execution.Execute(queries.First);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
        }
    }

    public static void RebuildFilteredSummaries(World world, Query bodies,
        BoundsKeySummary<Hair, Entity> keys, CategorySummary categories)
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().WithOptionalRead<Hair>().OpenRead();
        keys.Rebuild(world);
        categories.Rebuild(world);
    }

    public static bool Filtered<TOutput>(World world, Query bodies, BoundsTree tree,
        BoundsKeySummary<Hair, Entity> keys, CategorySummary categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().WithOptionalRead<Hair>().OpenRead();
        var execution = new FilteredPairs<TOutput>(world, tree, keys, categories, output);
        try { execution.Execute(scope); return execution.Completed; }
        finally { output = execution.Output; }
    }

    private ref struct FilteredPairs<TOutput>
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly World _world;
        private readonly BoundsTree _tree;
        private readonly BoundsKeySummary<Hair, Entity> _keys;
        private readonly CategorySummary _categories;
        private readonly BoundsNodePair _partition;

        public FilteredPairs(World world, BoundsTree tree, BoundsKeySummary<Hair, Entity> keys,
            CategorySummary categories, TOutput output)
            : this(world, tree, keys, categories, output, new BoundsNodePair(tree.Root, tree.Root)) { }

        public FilteredPairs(World world, BoundsTree tree, BoundsKeySummary<Hair, Entity> keys,
            CategorySummary categories, TOutput output, BoundsNodePair partition)
        {
            _world = world;
            _tree = tree;
            _keys = keys;
            _categories = categories;
            _partition = partition;
            Output = output;
            Completed = false;
        }

        public TOutput Output;
        public bool Completed;

        public void Execute(scoped QueryScope members) => Completed = Traverse(members);

        private bool Traverse(scoped QueryScope members)
        {
            using ReadLookup<Body> values = _world.OpenRead<Body>();
            if (_tree.Root < 0)
                return true;
            ReadOnlySpan<BoundsNode> nodes = _tree.Nodes;
            Span<FilteredNodePair> nodePairs = stackalloc FilteredNodePair[2 * _tree.Height + 1];
            int pairCount = 0;
            nodePairs[pairCount++] = new(_partition.First, _partition.Second, default, default);
            while (pairCount > 0)
            {
                FilteredNodePair pair = nodePairs[--pairCount];
                ref readonly BoundsNode first = ref nodes[pair.First];
                ref readonly BoundsNode second = ref nodes[pair.Second];
                if (pair.First == pair.Second && first.IsLeaf ||
                    pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                    continue;
                GroupMatch forward = Groups(pair.First, pair.Second, in pair.Forward, out var forwardResults);
                GroupMatch reverse = Groups(pair.Second, pair.First, in pair.Reverse, out var reverseResults);
                if (forward == GroupMatch.None && reverse == GroupMatch.None)
                    continue;
                if (pair.First == pair.Second)
                {
                    nodePairs[pairCount++] = new(first.SecondChild, first.SecondChild, forwardResults, reverseResults);
                    nodePairs[pairCount++] = new(first.FirstChild, first.SecondChild, forwardResults, reverseResults);
                    nodePairs[pairCount++] = new(first.FirstChild, first.FirstChild, forwardResults, reverseResults);
                }
                else if (first.IsLeaf && second.IsLeaf)
                {
                    bool ascending = EntityOrder.Precedes(first.Entity, second.Entity);
                    int firstNode = ascending ? pair.First : pair.Second;
                    int secondNode = ascending ? pair.Second : pair.First;
                    GroupMatch match = ascending ? forward : reverse;
                    GroupResults results = ascending ? forwardResults : reverseResults;
                    if (match == GroupMatch.None)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.Contains(entities.Item1) || !members.Contains(entities.Item2))
                        continue;
                    if (values.ReadKnown(entities.Item1).Kind != MotionKind.Dynamic && values.ReadKnown(entities.Item2).Kind != MotionKind.Dynamic)
                        continue;
                    if (results.Item1.Item2 == GroupMatch.Unknown)
                    {
                        ref readonly var firstBits = ref _categories.Nodes[firstNode];
                        ref readonly var secondBits = ref _categories.Nodes[secondNode];
                        if (firstBits.ComponentCount == 0 || secondBits.ComponentCount == 0 ||
                            (firstBits.AllowedAny & secondBits.CategoriesAny) == 0 ||
                            (secondBits.AllowedAny & firstBits.CategoriesAny) == 0)
                            continue;
                    }
                    if (results.Item2 == GroupMatch.Unknown)
                    {
                        ref readonly var firstKey = ref _keys.Nodes[firstNode];
                        ref readonly var secondKey = ref _keys.Nodes[secondNode];
                        if (firstKey.KeyCount != 0 && secondKey.KeyCount != 0 && firstKey.UniformKey == secondKey.UniformKey)
                            continue;
                    }
                    if (!Output.Add(in entities))
                        return false;
                }
                else if (second.IsLeaf || !first.IsLeaf && first.Height >= second.Height)
                {
                    nodePairs[pairCount++] = new(first.SecondChild, pair.Second, forwardResults, reverseResults);
                    nodePairs[pairCount++] = new(first.FirstChild, pair.Second, forwardResults, reverseResults);
                }
                else
                {
                    nodePairs[pairCount++] = new(pair.First, second.SecondChild, forwardResults, reverseResults);
                    nodePairs[pairCount++] = new(pair.First, second.FirstChild, forwardResults, reverseResults);
                }
            }
            return true;
        }

        private GroupMatch Groups(int firstNode, int secondNode, in GroupResults inherited, out GroupResults results)
        {
            GroupMatch categories = inherited.Item1.Item2;
            if (categories == GroupMatch.Unknown)
            {
                ref readonly var first = ref _categories.Nodes[firstNode];
                ref readonly var second = ref _categories.Nodes[secondNode];
                if ((first.AllowedAny & second.CategoriesAny) == 0 || (second.AllowedAny & first.CategoriesAny) == 0)
                    categories = GroupMatch.None;
                else if (first.ComponentCount == _tree.Nodes[firstNode].MemberCount &&
                    second.ComponentCount == _tree.Nodes[secondNode].MemberCount &&
                    (first.AllowedCommon & second.CategoriesCommon) != 0 &&
                    (second.AllowedCommon & first.CategoriesCommon) != 0)
                    categories = GroupMatch.All;
            }

            GroupMatch hair = inherited.Item2;
            if (hair == GroupMatch.Unknown)
            {
                ref readonly var first = ref _keys.Nodes[firstNode];
                ref readonly var second = ref _keys.Nodes[secondNode];
                if (first.KeyCount == 0 || second.KeyCount == 0)
                    hair = GroupMatch.None;
                else if (first.HasUniformKey && second.HasUniformKey)
                {
                    if (first.UniformKey != second.UniformKey)
                        hair = GroupMatch.None;
                    else if (first.KeyCount == _tree.Nodes[firstNode].MemberCount &&
                        second.KeyCount == _tree.Nodes[secondNode].MemberCount)
                        hair = GroupMatch.All;
                }
            }
            results = ((GroupMatch.Unknown, categories), hair);
            return categories == GroupMatch.None || hair == GroupMatch.All ? GroupMatch.None : GroupMatch.Unknown;
        }
    }

    private readonly struct FilteredNodePair(int first, int second, GroupResults forward, GroupResults reverse)
    {
        internal readonly int First = first;
        internal readonly int Second = second;
        internal readonly GroupResults Forward = forward;
        internal readonly GroupResults Reverse = reverse;
    }
}
