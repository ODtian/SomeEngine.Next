using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

internal static class ManagedComponentChecks
{
    internal static void VerifyAll()
    {
        TableReferencesRemainReadableAfterCollection();
        SparseReferencesRemainReadableAfterCollection();
        foreach (bool sparse in new[] { false, true })
        {
            ReferenceFieldsCanBeWrittenThroughTraversal(sparse);
            ReplacingReferencesReleasesPreviousValues(sparse);
            EntityCopiesRetainReferencesAndCanChangeIndependently(sparse);
            RemovingReferencesReleasesValuesAfterRemovalFactsAreCleared(sparse);
            ReferencesSurviveGrowthAndTableChanges(sparse);
        }
    }

    internal static void TableReferencesRemainReadableAfterCollection()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        ReferenceObservers references = AddTableReferences(world, entity);

        Collect();

        CheckReferences(world.Read<TableReferences>(entity), references);
    }

    internal static void SparseReferencesRemainReadableAfterCollection()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        ReferenceObservers references = AddSparseReferences(world, entity);

        Collect();

        CheckReferences(world.ReadSparse<SparseReferences>(entity), references);
    }

    internal static void ReferenceFieldsCanBeWrittenThroughTraversal(bool sparse)
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        ReferenceObservers previous = AddReferences(world, entity, sparse);
        Collect();

        ReferenceObservers current = WriteReferences(world, sparse);
        CollectYoung();
        CheckStoredReferences(world, entity, sparse, current);
        Collect();

        CheckStoredReferences(world, entity, sparse, current);
        CheckReleased(previous);
        CheckTraversal(world, entity, sparse, current);
    }

    internal static void ReplacingReferencesReleasesPreviousValues(bool sparse)
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        ReferenceObservers previous = AddReferences(world, entity, sparse);
        Collect();

        ReferenceObservers current = ReplaceReferences(world, entity, sparse);
        CollectYoung();
        CheckStoredReferences(world, entity, sparse, current);
        Collect();

        CheckStoredReferences(world, entity, sparse, current);
        CheckReleased(previous);
    }

    internal static void EntityCopiesRetainReferencesAndCanChangeIndependently(bool sparse)
    {
        using var world = new World();
        Entity source = world.CreateEntity();
        ReferenceObservers original = AddReferences(world, source, sparse);
        Entity target = world.CreateEntity();
        ReferenceObservers previousTarget = AddReferences(world, target, sparse);

        Entity clone = world.CloneEntity(source);
        world.CopyEntity(source, target);
        world.DestroyEntity(source);
        Collect();

        CheckStoredReferences(world, clone, sparse, original);
        CheckStoredReferences(world, target, sparse, original);
        CheckReleased(previousTarget);

        ReferenceObservers changed = ReplaceReferences(world, clone, sparse);
        Collect();
        CheckStoredReferences(world, clone, sparse, changed);
        CheckStoredReferences(world, target, sparse, original);

        world.DestroyEntity(target);
        Collect();
        CheckReleased(original);
        CheckStoredReferences(world, clone, sparse, changed);
    }

    internal static void RemovingReferencesReleasesValuesAfterRemovalFactsAreCleared(bool sparse)
    {
        using var world = new World();
        Entity removed = world.CreateEntity();
        ReferenceObservers released = AddReferences(world, removed, sparse);
        Entity survivor = world.CreateEntity();
        ReferenceObservers retained = AddReferences(world, survivor, sparse);

        if (sparse)
        {
            world.RemoveSparse<SparseReferences>(removed);
            Require(!world.HasSparse<SparseReferences>(removed), "Removed sparse component is still present.");
        }
        else
        {
            world.Remove<TableReferences>(removed);
            Require(!world.Has<TableReferences>(removed), "Removed table component is still present.");
            Collect();
            CheckTableRemoval(world, removed, released);
            ClearTableRemoval(world, removed);
            Require(!world.Has<Removed<TableReferences>>(removed), "Cleared removal fact is still present.");
        }

        Collect();
        CheckReleased(released);
        CheckStoredReferences(world, survivor, sparse, retained);
        CheckTraversal(world, survivor, sparse, retained);
    }

    internal static void ReferencesSurviveGrowthAndTableChanges(bool sparse)
    {
        using var world = new World();
        var entities = new Entity[1_100];
        var references = new ReferenceObservers[entities.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity();
            references[i] = AddReferences(world, entities[i], sparse);
        }

        if (!sparse)
        {
            foreach (Entity entity in entities)
                world.Add(entity, new Number(5));
            foreach (Entity entity in entities)
                world.Remove<Number>(entity);
        }

        Collect();
        for (int i = 0; i < entities.Length; i++)
            CheckStoredReferences(world, entities[i], sparse, references[i]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckTableRemoval(World world, Entity entity, ReferenceObservers references) =>
        CheckReferences(world.Read<Removed<TableReferences>>(entity).Value, references);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearTableRemoval(World world, Entity entity)
    {
        uint version = world.Read<Removed<TableReferences>>(entity).Version;
        world.ClearRemoved<TableReferences>(version);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReferenceObservers ReplaceReferences(World world, Entity entity, bool sparse)
    {
        TableReferences value = CreateTableReferences();
        if (sparse)
            world.ReplaceSparse(entity, new SparseReferences(value.Text, value.Callback, value.Data));
        else
            world.Replace(entity, value);

        return new(value.Text, value.Callback, value.Data);
    }

    private static ReferenceObservers AddReferences(World world, Entity entity, bool sparse) =>
        sparse ? AddSparseReferences(world, entity) : AddTableReferences(world, entity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReferenceObservers WriteReferences(World world, bool sparse)
    {
        TableReferences value = CreateTableReferences();
        if (sparse)
        {
            world.ExecuteSparseWrite<SparseReferences>((_, values) =>
            {
                foreach (ref SparseReferences destination in values)
                {
                    destination.Text = value.Text;
                    destination.Callback = value.Callback;
                    destination.Data = value.Data;
                }
            });
        }
        else
        {
            var query = world.Query(world.QueryDefinition().ReadWrite<TableReferences>());
            world.ExecuteQuery(query, rows =>
            {
                foreach (var row in rows.Rows)
                {
                    ref TableReferences destination = ref row.ReadWrite<TableReferences>();
                    destination.Text = value.Text;
                    destination.Callback = value.Callback;
                    destination.Data = value.Data;
                }
            });
        }

        return new(value.Text, value.Callback, value.Data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckStoredReferences(World world, Entity entity, bool sparse, ReferenceObservers references)
    {
        if (sparse)
            CheckReferences(world.ReadSparse<SparseReferences>(entity), references);
        else
            CheckReferences(world.Read<TableReferences>(entity), references);
    }

    private static void CheckTraversal(World world, Entity entity, bool sparse, ReferenceObservers references)
    {
        if (sparse)
        {
            world.ExecuteSparseRead<SparseReferences>((entities, values) =>
            {
                Require(entities.SequenceEqual(new[] { entity }), "Sparse traversal returned different entities.");
                CheckReferences(values[0], references);
            });
        }
        else
        {
            var observed = new List<Entity>();
            var query = world.Query(world.QueryDefinition().Read<TableReferences>());
            world.ExecuteQuery(query, rows =>
            {
                foreach (var row in rows.Rows)
                {
                    observed.Add(row.Entity);
                    CheckReferences(row.Read<TableReferences>(), references);
                }
            });
            Require(observed.SequenceEqual(new[] { entity }), "Table traversal returned different entities.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckReleased(ReferenceObservers references)
    {
        Require(!references.Text.TryGetTarget(out _), "Released string remains reachable.");
        Require(!references.Callback.TryGetTarget(out _), "Released delegate remains reachable.");
        Require(!references.Data.TryGetTarget(out _), "Released object remains reachable.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReferenceObservers AddTableReferences(World world, Entity entity)
    {
        TableReferences value = CreateTableReferences();
        world.Add(entity, value);
        return new(value.Text, value.Callback, value.Data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReferenceObservers AddSparseReferences(World world, Entity entity)
    {
        TableReferences fields = CreateTableReferences();
        var value = new SparseReferences(fields.Text, fields.Callback, fields.Data);
        world.AddSparse(entity, value);
        return new(value.Text, value.Callback, value.Data);
    }

    private static TableReferences CreateTableReferences() =>
        new(new string('m', 33), new ReferenceData(71).ReadValue, new ReferenceData(42));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckReferences(TableReferences value, ReferenceObservers references)
    {
        Require(value.Text == new string('m', 33), "Stored string value changed.");
        Require(value.Callback() == 71, "Stored instance delegate changed.");
        Require(value.Data.Value == 42, "Stored object value changed.");
        Require(references.Text.TryGetTarget(out string? text) && ReferenceEquals(value.Text, text),
            "Stored string reference was released.");
        Require(references.Callback.TryGetTarget(out Func<int>? callback) && ReferenceEquals(value.Callback, callback),
            "Stored delegate reference was released.");
        Require(references.Data.TryGetTarget(out ReferenceData? data) && ReferenceEquals(value.Data, data),
            "Stored object reference was released.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckReferences(SparseReferences value, ReferenceObservers references) =>
        CheckReferences(new TableReferences(value.Text, value.Callback, value.Data), references);

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void CollectYoung() =>
        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private record struct TableReferences(string Text, Func<int> Callback, ReferenceData Data) : IComponent;

    private record struct SparseReferences(string Text, Func<int> Callback, ReferenceData Data) : ISparseComponent;

    private readonly record struct Number(int Value) : IComponent;

    private sealed class ReferenceData(int value)
    {
        internal int Value { get; } = value;
        internal int ReadValue() => Value;
    }

    private sealed class ReferenceObservers(string text, Func<int> callback, ReferenceData data)
    {
        internal WeakReference<string> Text { get; } = new(text);
        internal WeakReference<Func<int>> Callback { get; } = new(callback);
        internal WeakReference<ReferenceData> Data { get; } = new(data);
    }
}
