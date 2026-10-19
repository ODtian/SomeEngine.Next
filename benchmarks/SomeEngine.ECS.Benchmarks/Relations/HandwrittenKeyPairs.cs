using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations;

/// <summary>Handwritten within-query pairing over the existing component-key buckets.</summary>
public static class HandwrittenKeyPairs<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    public static bool Where<TOutput>(World world, QueryHandle members, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate(world, members, false, ref output);

    public static bool Except<TOutput>(World world, QueryHandle members, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate(world, members, true, ref output);

    private static bool Enumerate<TOutput>(World world, QueryHandle members, bool exclude, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        QueryDefinition original = world.GetQueryDefinition(members);
        QueryDefinition definition = original.WithOptionalRead<TComponent>();
        bool temporaryQuery = definition.CanWrite || !ReferenceEquals(original, definition);
        QueryHandle query = temporaryQuery ? world.Query(definition) : members;
        var execution = new MemberExecution<TOutput>(world, exclude, output);
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

    private ref struct MemberExecution<TOutput> : IQueryExecution
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly World _world;
        private readonly bool _exclude;

        public MemberExecution(World world, bool exclude, TOutput output)
        {
            _world = world;
            _exclude = exclude;
            Output = output;
        }

        public TOutput Output;
        public bool Completed { get; private set; }

        public void Execute(QueryCursor members)
        {
            var keys = new HashSet<TKey>();
            var missing = new List<Entity>();
            foreach (var member in members.Rows)
            {
                if (member.Has<TComponent>())
                    keys.Add(member.ReadRef<TComponent>().GetKey());
                else
                    missing.Add(member.Entity);
            }

            var firstKeys = keys.GetEnumerator();
            bool firstMissing = false;
            while (true)
            {
                bool firstHasKey = firstKeys.MoveNext();
                if (!firstHasKey && (firstMissing || missing.Count == 0))
                    break;
                firstMissing = !firstHasKey;
                TKey firstKey = firstHasKey ? firstKeys.Current : default!;
                ReadOnlySpan<Entity> firstEntities = firstHasKey
                    ? _world.GetByIndex<TComponent, TKey>(firstKey) : CollectionsMarshal.AsSpan(missing);
                var secondKeys = keys.GetEnumerator();
                bool secondMissing = false;
                while (true)
                {
                    bool secondHasKey = secondKeys.MoveNext();
                    if (!secondHasKey && (secondMissing || missing.Count == 0))
                        break;
                    secondMissing = !secondHasKey;
                    TKey secondKey = secondHasKey ? secondKeys.Current : default!;
                    ReadOnlySpan<Entity> secondEntities = secondHasKey
                        ? _world.GetByIndex<TComponent, TKey>(secondKey) : CollectionsMarshal.AsSpan(missing);
                    bool matches = firstHasKey && secondHasKey && firstKey.Equals(secondKey);
                    if (matches == _exclude)
                        continue;
                    foreach (Entity first in firstEntities)
                    {
                        if (!members.Contains(first))
                            continue;
                        foreach (Entity second in secondEntities)
                        {
                            bool ascending = first.Index < second.Index ||
                                first.Index == second.Index && first.Generation < second.Generation;
                            if (!ascending || !members.Contains(second))
                                continue;
                            var pair = (first, second);
                            if (!Output.Add(in pair))
                            {
                                Completed = false;
                                return;
                            }
                        }
                    }
                }
            }
            Completed = true;
        }
    }
}
