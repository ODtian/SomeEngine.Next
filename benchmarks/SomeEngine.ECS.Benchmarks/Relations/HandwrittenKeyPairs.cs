using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations;

/// <summary>Handwritten within-query pairing over the existing component-key buckets.</summary>
public static class HandwrittenKeyPairs<TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    public static bool Where<TOutput>(World world, Query members, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate(world, members, false, ref output);

    public static bool Except<TOutput>(World world, Query members, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
        => Enumerate(world, members, true, ref output);

    private static bool Enumerate<TOutput>(World world, Query members, bool exclude, ref TOutput output)
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var index = world.Index<TComponent, TKey>();
        using QueryScope reads = world.Query(new QueryDescription().Optional<TComponent>()).OpenRead();
        using QueryScope query = members.OpenRead();
        var execution = new MemberExecution<TOutput>(index, exclude, output);
        try
        {
            execution.Execute(query);
            return execution.Completed;
        }
        finally { output = execution.Output; }
    }

    private ref struct MemberExecution<TOutput>
        where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
    {
        private readonly ValueIndex<TComponent, TKey> _index;
        private readonly bool _exclude;

        public MemberExecution(ValueIndex<TComponent, TKey> index, bool exclude, TOutput output)
        {
            _index = index;
            _exclude = exclude;
            Output = output;
        }

        public TOutput Output;
        public bool Completed { get; private set; }

        public void Execute(scoped QueryScope members)
        {
            var keys = new HashSet<TKey>();
            var missing = new List<Entity>();
            foreach (QueryChunk chunk in members)
            {
                chunk.TryRead<TComponent>(out ReadOnlySpan<TComponent> values);
                for (int row = 0; row < chunk.Count; row++)
                    if (!values.IsEmpty && chunk.Has<TComponent>(row)) keys.Add(values[row].GetKey());
                    else missing.Add(chunk.Entities[row]);
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
                IndexMembers firstMembers = firstHasKey ? _index.Find(firstKey) : default;
                try
                {
                    ReadOnlySpan<Entity> firstEntities = firstHasKey
                        ? firstMembers.Entities : CollectionsMarshal.AsSpan(missing);
                    var secondKeys = keys.GetEnumerator();
                    bool secondMissing = false;
                    while (true)
                    {
                        bool secondHasKey = secondKeys.MoveNext();
                        if (!secondHasKey && (secondMissing || missing.Count == 0))
                            break;
                        secondMissing = !secondHasKey;
                        TKey secondKey = secondHasKey ? secondKeys.Current : default!;
                        IndexMembers secondMembers = secondHasKey ? _index.Find(secondKey) : default;
                        try
                        {
                            ReadOnlySpan<Entity> secondEntities = secondHasKey
                                ? secondMembers.Entities : CollectionsMarshal.AsSpan(missing);
                            bool matches = firstHasKey && secondHasKey && firstKey.Equals(secondKey);
                            if (matches == _exclude)
                                continue;
                            foreach (Entity first in firstEntities)
                            {
                                if (!members.Contains(first))
                                    continue;
                                foreach (Entity second in secondEntities)
                                {
                                    bool ascending = first.Id < second.Id ||
                                        first.Id == second.Id && first.Generation < second.Generation;
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
                        finally { if (secondHasKey) secondMembers.Dispose(); }
                    }
                }
                finally { if (firstHasKey) firstMembers.Dispose(); }
            }
            Completed = true;
        }
    }
}
