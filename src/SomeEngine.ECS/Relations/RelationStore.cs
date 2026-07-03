using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Relations;

internal sealed class RelationStore<T> : IRelationStore
    where T : struct, IRelation
{
    private static readonly bool s_isExclusive = default(T) is IExclusiveRelation;

    private readonly Dictionary<Entity, SmallList<RelationEntry<T>>> _forward = new();
    private readonly Dictionary<Entity, SmallList<Entity>> _reverse = new();
    private readonly List<RelationChange<T>> _changes = new();

    public int RelationTagId => ComponentMetadata<RelationTag<T>>.Id;

    public void Add(Entity source, Entity target, in T value, uint version)
    {
        if (s_isExclusive)
        {
            AddExclusive(source, target, value, version);
            return;
        }

        AddMany(source, target, value, version);
    }

    public void Replace(Entity source, Entity target, in T value, uint version)
    {
        if (s_isExclusive)
        {
            ReplaceExclusive(source, target, value, version);
            return;
        }

        ReplaceMany(source, target, value, version);
    }

    public bool Remove(Entity source, Entity target, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        if (Unsafe.IsNullRef(ref relations))
            return false;

        int relationIndex = FindTarget(relations.AsSpan(), target);
        if (relationIndex < 0)
            return false;

        var oldValue = relations[relationIndex].Value;
        relations.SwapRemoveAt(relationIndex);
        RemoveReverse(target, source);
        Write(
            RelationChangeKind.Removed,
            source,
            target,
            target,
            default,
            oldValue,
            version);

        if (relations.Count == 0)
            _forward.Remove(source);

        return true;
    }

    public bool RemoveAll(Entity source, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        if (Unsafe.IsNullRef(ref relations) || relations.Count == 0)
            return false;

        var snapshot = relations;
        _forward.Remove(source);

        foreach (var relation in snapshot.AsSpan())
        {
            RemoveReverse(relation.Target, source);
            Write(
                RelationChangeKind.Removed,
                source,
                relation.Target,
                relation.Target,
                default,
                relation.Value,
                version);
        }

        return true;
    }

    public bool Has(Entity source, Entity target)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        return !Unsafe.IsNullRef(ref relations) && FindTarget(relations.AsSpan(), target) >= 0;
    }

    public ReadOnlySpan<RelationEntry<T>> GetRelations(Entity source)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        return Unsafe.IsNullRef(ref relations) ? ReadOnlySpan<RelationEntry<T>>.Empty : relations.AsSpan();
    }

    public ReadOnlySpan<Entity> GetSources(Entity target)
    {
        ref var sources = ref CollectionsMarshal.GetValueRefOrNullRef(_reverse, target);
        return Unsafe.IsNullRef(ref sources) ? ReadOnlySpan<Entity>.Empty : sources.AsSpan();
    }

    public bool HasOutgoing(Entity source)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        return !Unsafe.IsNullRef(ref relations) && relations.Count != 0;
    }

    public ReadOnlySpan<RelationChange<T>> Changes(uint lastVersion)
    {
        var changes = CollectionsMarshal.AsSpan(_changes);
        for (int i = 0; i < changes.Length; i++)
        {
            if (VersionClock.IsNewer(changes[i].Version, lastVersion))
                return changes[i..];
        }

        return ReadOnlySpan<RelationChange<T>>.Empty;
    }

    public void OnEntityDestroyed(Entity entity, uint version, List<RelationDrop> drops)
    {
        RemoveAll(entity, version);

        while (_reverse.TryGetValue(entity, out var incoming) && incoming.Count > 0)
        {
            var source = incoming[0];
            if (!Remove(source, entity, version))
            {
                RemoveReverse(entity, source);
                continue;
            }

            if (!HasOutgoing(source))
                drops.Add(new RelationDrop(source, RelationTagId));
        }
    }

    public void RemoveAllOutgoing(SomeEngine.ECS.Owners.Relations relations, Entity source)
    {
        relations.RemoveAll<T>(source);
    }

    public void AddOutgoingCopy(SomeEngine.ECS.Owners.Relations relations, Entity source, Entity target)
    {
        var outgoing = GetRelations(source);
        for (int i = 0; i < outgoing.Length; i++)
            relations.Add(target, outgoing[i].Target, outgoing[i].Value);
    }

    private void AddExclusive(Entity source, Entity target, in T value, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrAddDefault(_forward, source, out bool exists);
        if (!exists)
            relations = default;

        if (relations.Count != 0)
            throw new InvalidOperationException(
                $"Entity {source} already has relation {typeof(T).Name}.");

        relations.Add(new RelationEntry<T>(target, value));
        AddReverse(target, source);
        Write(
            RelationChangeKind.Added,
            source,
            target,
            Entity.Null,
            value,
            default,
            version);
    }

    private void ReplaceExclusive(Entity source, Entity target, in T value, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        if (Unsafe.IsNullRef(ref relations) || relations.Count == 0)
            throw new InvalidOperationException(
                $"Entity {source} does not have relation {typeof(T).Name}.");

        var oldTarget = relations[0].Target;
        var oldValue = relations[0].Value;
        if (oldTarget != target)
        {
            RemoveReverse(oldTarget, source);
            AddReverse(target, source);
        }

        relations[0] = new RelationEntry<T>(target, value);
        Write(
            RelationChangeKind.Changed,
            source,
            target,
            oldTarget,
            value,
            oldValue,
            version);
    }

    private void AddMany(Entity source, Entity target, in T value, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrAddDefault(_forward, source, out bool exists);
        if (!exists)
            relations = default;

        int relationIndex = FindTarget(relations.AsSpan(), target);
        if (relationIndex >= 0)
            throw new InvalidOperationException(
                $"Entity {source} already has relation {typeof(T).Name} to {target}.");

        relations.Add(new RelationEntry<T>(target, value));
        AddReverse(target, source);
        Write(
            RelationChangeKind.Added,
            source,
            target,
            Entity.Null,
            value,
            default,
            version);
    }

    private void ReplaceMany(Entity source, Entity target, in T value, uint version)
    {
        ref var relations = ref CollectionsMarshal.GetValueRefOrNullRef(_forward, source);
        if (Unsafe.IsNullRef(ref relations))
            throw new InvalidOperationException(
                $"Entity {source} does not have relation {typeof(T).Name} to {target}.");

        int relationIndex = FindTarget(relations.AsSpan(), target);
        if (relationIndex < 0)
            throw new InvalidOperationException(
                $"Entity {source} does not have relation {typeof(T).Name} to {target}.");

        var oldValue = relations[relationIndex].Value;
        relations[relationIndex] = new RelationEntry<T>(target, value);
        Write(
            RelationChangeKind.Changed,
            source,
            target,
            target,
            value,
            oldValue,
            version);
    }

    private void AddReverse(Entity target, Entity source)
    {
        ref var sources = ref CollectionsMarshal.GetValueRefOrAddDefault(_reverse, target, out bool exists);
        if (!exists)
            sources = default;

        if (sources.IndexOf(source) < 0)
            sources.Add(source);
    }

    private void RemoveReverse(Entity target, Entity source)
    {
        ref var sources = ref CollectionsMarshal.GetValueRefOrNullRef(_reverse, target);
        if (Unsafe.IsNullRef(ref sources))
            return;

        if (!sources.RemoveSwapBack(source))
            return;

        if (sources.Count == 0)
            _reverse.Remove(target);
    }

    private static int FindTarget(ReadOnlySpan<RelationEntry<T>> relations, Entity target)
    {
        for (int i = 0; i < relations.Length; i++)
        {
            if (relations[i].Target == target)
                return i;
        }

        return -1;
    }

    private void Write(
        RelationChangeKind kind,
        Entity source,
        Entity target,
        Entity oldTarget,
        in T value,
        in T oldValue,
        uint version)
    {
        _changes.Add(new RelationChange<T>(
            kind,
            source,
            target,
            oldTarget,
            value,
            oldValue,
            version));
    }

}

