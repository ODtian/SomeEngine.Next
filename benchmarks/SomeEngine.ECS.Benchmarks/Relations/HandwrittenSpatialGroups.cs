using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;
using GroupResults = System.ValueTuple<System.ValueTuple<SomeEngine.Physics.GroupMatch, SomeEngine.Physics.GroupMatch>, SomeEngine.Physics.GroupMatch>;
using CategorySummary = SomeEngine.Physics.CollisionBitmapSummary<
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategories,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.MutualCategoryBitmap,
    SomeEngine.ECS.Benchmarks.Relations.Spatial.Body, SomeEngine.ECS.Relations.MutualPermission>;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public static partial class HandwrittenSpatialCollision
{
    public static bool EnumeratePartition<TOutput>(CollisionNodePair partition, RelationQueryViews queries,
        CollisionTree tree, CollisionKeySummary<Hair, Entity> keys, CategorySummary categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var execution = new FilteredPairs<TOutput>(tree, keys, categories, output, partition);
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

    public static void RebuildFilteredSummaries(World world, QueryHandle bodies,
        CollisionKeySummary<Hair, Entity> keys, CategorySummary categories)
    {
        QueryHandle access = world.Query(QueryDefinition.Empty.WithAccessFrom(world.GetQueryDefinition(bodies))
            .WithOptionalRead<Body>().WithOptionalRead<Hair>());
        var execution = new FilteredSummaryBuild(keys, categories);
        try
        {
            world.ExecuteQuery(access, ref execution);
        }
        finally
        {
            world.ReleaseQuery(access);
        }
    }

    public static bool Filtered<TOutput>(World world, QueryHandle bodies, CollisionTree tree,
        CollisionKeySummary<Hair, Entity> keys, CategorySummary categories, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition original = world.GetQueryDefinition(bodies);
        QueryDefinition definition = original.WithOptionalRead<Body>().WithOptionalRead<Hair>();
        bool temporaryQuery = definition.CanWrite || !ReferenceEquals(original, definition);
        QueryHandle query = temporaryQuery ? world.Query(definition) : bodies;
        var execution = new FilteredPairs<TOutput>(tree, keys, categories, output);
        try
        {
            world.ExecuteQuery(query, ref execution);
            return execution.Completed;
        }
        finally
        {
            output = execution.Output;
            if (temporaryQuery)
                world.ReleaseQuery(query);
        }
    }

    private struct FilteredSummaryBuild(CollisionKeySummary<Hair, Entity> keys, CategorySummary categories) : IQueryExecution
    {
        public void Execute(QueryCursor access)
        {
            keys.Rebuild(access);
            categories.Rebuild(access);
        }
    }

    private ref struct FilteredPairs<TOutput> : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly CollisionTree _tree;
        private readonly CollisionKeySummary<Hair, Entity> _keys;
        private readonly CategorySummary _categories;
        private readonly CollisionNodePair _partition;

        public FilteredPairs(CollisionTree tree, CollisionKeySummary<Hair, Entity> keys,
            CategorySummary categories, TOutput output)
            : this(tree, keys, categories, output, new CollisionNodePair(tree.Root, tree.Root)) { }

        public FilteredPairs(CollisionTree tree, CollisionKeySummary<Hair, Entity> keys,
            CategorySummary categories, TOutput output, CollisionNodePair partition)
        {
            _tree = tree;
            _keys = keys;
            _categories = categories;
            _partition = partition;
            Output = output;
            Completed = false;
        }

        public TOutput Output;
        public bool Completed;

        public void Execute(QueryCursor members) => Completed = Traverse(members);

        private bool Traverse(QueryCursor members)
        {
            if (_tree.Root < 0)
                return true;
            ReadOnlySpan<CollisionNode> nodes = _tree.Nodes;
            Span<FilteredNodePair> nodePairs = stackalloc FilteredNodePair[2 * _tree.Height + 1];
            int pairCount = 0;
            nodePairs[pairCount++] = new(_partition.First, _partition.Second, default, default);
            while (pairCount > 0)
            {
                FilteredNodePair pair = nodePairs[--pairCount];
                ref readonly CollisionNode first = ref nodes[pair.First];
                ref readonly CollisionNode second = ref nodes[pair.Second];
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
                    if (!members.TryGetRow(entities.Item1, out var firstRow) || !members.TryGetRow(entities.Item2, out var secondRow))
                        continue;
                    if (firstRow.ReadRef<Body>().Kind != MotionKind.Dynamic && secondRow.ReadRef<Body>().Kind != MotionKind.Dynamic)
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
