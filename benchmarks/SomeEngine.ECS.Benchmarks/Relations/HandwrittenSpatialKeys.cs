using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

/// <summary>Direct character-key exclusion over the same collision nodes and key summaries.</summary>
public static class HandwrittenSpatialKeys
{
    public static bool Except<TOutput>(World world, QueryHandle bodies, CollisionTree tree,
        ReadOnlySpan<CollisionKeyData<Entity>> keys, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition definition = world.GetQueryDefinition(bodies);
        QueryHandle members = world.Query(definition);
        QueryHandle access = world.Query(QueryDefinition.Empty.WithAccessFrom(definition)
            .WithOptionalRead<Body>().WithOptionalRead<Hair>());
        var execution = new KeyAccess<TOutput>(world, members, tree, keys, output);
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

    private ref struct KeyAccess<TOutput>(World world, QueryHandle members, CollisionTree tree,
        ReadOnlySpan<CollisionKeyData<Entity>> keys, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionKeyData<Entity>> _keys = keys;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor access)
        {
            var execution = new KeyPairs<TOutput>(tree, _keys, Output);
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

    private ref struct KeyPairs<TOutput>(CollisionTree tree,
        ReadOnlySpan<CollisionKeyData<Entity>> keys, TOutput output) : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ReadOnlySpan<CollisionKeyData<Entity>> _keys = keys;
        public TOutput Output = output;
        public bool Completed;

        public void Execute(QueryCursor members) => Completed = Traverse(members);

        private bool Traverse(QueryCursor members)
        {
            if (tree.Root < 0)
                return true;
            ReadOnlySpan<CollisionNode> nodes = tree.Nodes;
            Span<KeyNodePair> work = stackalloc KeyNodePair[2 * tree.Height + 1];
            int count = 0;
            work[count++] = new(tree.Root, tree.Root, default, default);
            while (count > 0)
            {
                KeyNodePair pair = work[--count];
                ref readonly CollisionNode first = ref nodes[pair.First];
                ref readonly CollisionNode second = ref nodes[pair.Second];
                if (pair.First == pair.Second && first.IsLeaf ||
                    pair.First != pair.Second && !first.Bounds.Overlaps(in second.Bounds))
                    continue;
                GroupMatch forward = Groups(nodes, _keys, pair.First, pair.Second, pair.Forward);
                GroupMatch reverse = Groups(nodes, _keys, pair.Second, pair.First, pair.Reverse);
                if (forward == GroupMatch.All && reverse == GroupMatch.All)
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
                    if (match == GroupMatch.All)
                        continue;
                    var entities = (nodes[firstNode].Entity, nodes[secondNode].Entity);
                    if (!members.TryGetRow(entities.Item1, out _) || !members.TryGetRow(entities.Item2, out _))
                        continue;
                    if (match == GroupMatch.Unknown)
                    {
                        ref readonly var firstKey = ref _keys[firstNode];
                        ref readonly var secondKey = ref _keys[secondNode];
                        if (firstKey.KeyCount != 0 && secondKey.KeyCount != 0 && firstKey.UniformKey == secondKey.UniformKey)
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

    private static GroupMatch Groups(ReadOnlySpan<CollisionNode> nodes,
        ReadOnlySpan<CollisionKeyData<Entity>> keys, int firstNode, int secondNode, GroupMatch inherited)
    {
        if (inherited != GroupMatch.Unknown)
            return inherited;
        ref readonly var first = ref keys[firstNode];
        ref readonly var second = ref keys[secondNode];
        if (first.KeyCount == 0 || second.KeyCount == 0)
            return GroupMatch.None;
        if (first.HasUniformKey && second.HasUniformKey)
        {
            if (first.UniformKey != second.UniformKey)
                return GroupMatch.None;
            if (first.KeyCount == nodes[firstNode].MemberCount && second.KeyCount == nodes[secondNode].MemberCount)
                return GroupMatch.All;
        }
        return GroupMatch.Unknown;
    }

    private readonly struct KeyNodePair(int first, int second, GroupMatch forward, GroupMatch reverse)
    {
        public readonly int First = first;
        public readonly int Second = second;
        public readonly GroupMatch Forward = forward;
        public readonly GroupMatch Reverse = reverse;
    }
}
