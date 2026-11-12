using Arch.Core;
using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public struct ScoreValue : IAddedComponent, IRemovedComponent
{
    public int Value, Max;
}

public struct EasyScore : EasyEcs.IComponent, EasyEcs.ITrackChanges
{
    public ScoreValue Value;
    public ulong Added;
}

public struct ScoreHistory : Friflo.Engine.ECS.IComponent
{
    public ScoreValue Value;
    public ulong Added, Written;
}

public struct OldScore : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{
    public ScoreValue Value;
    public ulong Version;
}

internal struct ReadEasyScores(ulong since, bool added) : EasyEcs.IForEachBatch<EasyScore>
{
    public static int Writes => 0;
    internal int Count;
    public void Execute(EasyEcs.EntitySpan entities, Span<EasyScore> values)
    {
        if (!added) { Count += values.Length; return; }
        for (int i = 0; i < values.Length; i++) if (values[i].Added > since) Count++;
    }
}

internal struct ReadOldScores : EasyEcs.IForEachBatch<OldScore>
{
    public static int Writes => 0;
    internal int Count;
    internal long Total;
    public void Execute(EasyEcs.EntitySpan entities, Span<OldScore> values)
    {
        Count += values.Length;
        for (int i = 0; i < values.Length; i++) Total += values[i].Value.Value;
    }
}

internal sealed partial class SomeEngineWorld
{
    private Query? _changedScores, _addedScores, _removedScores;

    private void PrepareChanges(string workload)
    {
        if (_changedScores is not null) return;
        ChangeStride = workload == "changes-1" ? 100 : 4;
        if (!_world.Has<ScoreValue>(_entities[0])) _world.Set<ScoreValue>(_entities, new ScoreValue());
        _changedScores = _world.Query(new QueryDescription().WithChanged<ScoreValue>(0));
        _addedScores = _world.Query(new QueryDescription().WithAdded<ScoreValue>(0));
        _removedScores = _world.Query(new QueryDescription().With<Removed<ScoreValue>>());
    }

    internal override void ChangesCycle()
    {
        ulong before = _world.Version;
        for (int i = 0; i < Count; i += ChangeStride) _world.Set(_entities[i], new ScoreValue { Value = 42 });
        ChangedCount = CountQuery(_changedScores.GetValueOrDefault().Since(before));
        for (int i = 0; i < Count; i += ChangeStride)
        {
            _world.Remove<ScoreValue>(_entities[i]);
            _world.Add(_entities[i], new ScoreValue { Value = 99 });
        }
        AddedCount = CountQuery(_addedScores.GetValueOrDefault().Since(before));
        for (int i = 0; i < Count; i += ChangeStride) _world.Remove<ScoreValue>(_entities[i]);
        int removed = 0;
        long total = 0;
        using (var scope = _removedScores.GetValueOrDefault().Open())
            foreach (var rows in scope)
            {
                var values = rows.Read<Removed<ScoreValue>>();
                removed += values.Length;
                for (int i = 0; i < values.Length; i++) total += values[i].Value.Value;
            }
        RemovedCount = removed;
        ReadTotal = total;
        _world.ClearRemoved<ScoreValue>();
        for (int i = 0; i < Count; i += ChangeStride) _world.Add(_entities[i], new ScoreValue());
    }

    private static int CountQuery(Query query)
    {
        int count = 0;
        using var scope = query.Open();
        foreach (var rows in scope) count += rows.Count;
        return count;
    }
}

internal sealed partial class EasyEcsWorld
{
    private bool _scoresReady;
    private ulong _scoreVersion;

    private void PrepareChanges(string workload)
    {
        if (_scoresReady) return;
        ChangeStride = workload == "changes-1" ? 100 : 4;
        if (!_entities[0].Has<EasyScore>())
            foreach (var entity in _entities) entity.Set(new EasyScore { Added = ++_scoreVersion });
        _scoresReady = true;
    }

    internal override void ChangesCycle()
    {
        ulong before = _scoreVersion;
        ulong nativeBefore = _world.ChangeTick;
        _world.Update();
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new EasyScore { Value = new ScoreValue { Value = 42 }, Added = entity.Read<EasyScore>().Added });
        }
        var changedFilter = EasyEcs.QueryFilter.Create().Changed<EasyScore>(nativeBefore + 1);
        var changed = new ReadEasyScores(0, false);
        _world.ForEachBatch<EasyScore, ReadEasyScores>(ref changed, in changedFilter);
        ChangedCount = changed.Count;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new OldScore { Value = entity.Read<EasyScore>().Value, Version = ++_scoreVersion });
            entity.Remove<EasyScore>();
            entity.Set(new EasyScore { Value = new ScoreValue { Value = 99 }, Added = ++_scoreVersion });
        }
        var added = new ReadEasyScores(before, true);
        _world.ForEachBatch<EasyScore, ReadEasyScores>(ref added);
        AddedCount = added.Count;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new OldScore { Value = entity.Read<EasyScore>().Value, Version = ++_scoreVersion });
            entity.Remove<EasyScore>();
        }
        var removed = new ReadOldScores();
        _world.ForEachBatch<OldScore, ReadOldScores>(ref removed);
        RemovedCount = removed.Count;
        ReadTotal = removed.Total;
        _world.RemoveAll<OldScore>();
        for (int i = 0; i < Count; i += ChangeStride) _entities[i].Set(new EasyScore { Added = ++_scoreVersion });
    }
}

internal sealed partial class FrifloWorld
{
    private ArchetypeQuery<ScoreHistory>? _scores;
    private ArchetypeQuery<OldScore>? _removedScores;
    private EntityBatch? _removeOldScores;
    private ulong _scoreVersion;

    private void PrepareChanges(string workload)
    {
        if (_scores is not null) return;
        ChangeStride = workload == "changes-1" ? 100 : 4;
        if (!_entities[0].HasComponent<ScoreHistory>())
            foreach (var entity in _entities)
            {
                ulong version = ++_scoreVersion;
                entity.AddComponent(new ScoreHistory { Added = version, Written = version });
            }
        _scores = _world.Query<ScoreHistory>();
        _removedScores = _world.Query<OldScore>();
        _removeOldScores = new EntityBatch().Remove<OldScore>();
    }

    internal override void ChangesCycle()
    {
        ulong before = _scoreVersion;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            ref ScoreHistory score = ref _entities[i].GetComponent<ScoreHistory>();
            score.Value = new ScoreValue { Value = 42 };
            score.Written = ++_scoreVersion;
        }
        int changed = 0;
        foreach (var (scores, _) in _scores!.Chunks)
            foreach (ref readonly var score in scores.Span) if (score.Written > before) changed++;
        ChangedCount = changed;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.AddComponent(new OldScore { Value = entity.GetComponent<ScoreHistory>().Value, Version = ++_scoreVersion });
            entity.RemoveComponent<ScoreHistory>();
            ulong version = ++_scoreVersion;
            entity.AddComponent(new ScoreHistory { Value = new ScoreValue { Value = 99 }, Added = version, Written = version });
        }
        int added = 0;
        foreach (var (scores, _) in _scores!.Chunks)
            foreach (ref readonly var score in scores.Span) if (score.Added > before) added++;
        AddedCount = added;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.AddComponent(new OldScore { Value = entity.GetComponent<ScoreHistory>().Value, Version = ++_scoreVersion });
            entity.RemoveComponent<ScoreHistory>();
        }
        int removed = 0;
        long total = 0;
        foreach (var (scores, _) in _removedScores!.Chunks)
            foreach (ref readonly var score in scores.Span) { removed++; total += score.Value.Value; }
        RemovedCount = removed;
        ReadTotal = total;
        // Clear by the complete current fact membership, then restore the repeatable input state.
        _removedScores.Entities.ApplyBatch(_removeOldScores!);
        for (int i = 0; i < Count; i += ChangeStride)
        {
            ulong version = ++_scoreVersion;
            _entities[i].AddComponent(new ScoreHistory { Added = version, Written = version });
        }
    }
}

internal sealed partial class ArchWorld
{
    private bool _scoresReady;
    private Arch.Core.QueryDescription _scores, _removedScores;
    private ulong _scoreVersion;

    private void PrepareChanges(string workload)
    {
        if (_scoresReady) return;
        ChangeStride = workload == "changes-1" ? 100 : 4;
        if (!_world.Has<ScoreHistory>(_entities[0]))
            foreach (var entity in _entities)
            {
                ulong version = ++_scoreVersion;
                _world.Add(entity, new ScoreHistory { Added = version, Written = version });
            }
        _scores = new Arch.Core.QueryDescription().WithAll<ScoreHistory>();
        _removedScores = new Arch.Core.QueryDescription().WithAll<OldScore>();
        _scoresReady = true;
    }

    internal override void ChangesCycle()
    {
        ulong before = _scoreVersion;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            ref ScoreHistory score = ref _world.Get<ScoreHistory>(_entities[i]);
            score.Value = new ScoreValue { Value = 42 };
            score.Written = ++_scoreVersion;
        }
        int changed = 0;
        foreach (ref var chunk in _world.Query(in _scores).GetChunkIterator())
            foreach (ref readonly var score in chunk.GetSpan<ScoreHistory>()[..chunk.Count]) if (score.Written > before) changed++;
        ChangedCount = changed;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            var old = new OldScore { Value = _world.Get<ScoreHistory>(entity).Value, Version = ++_scoreVersion };
            if (_world.Has<OldScore>(entity)) _world.Set(entity, old); else _world.Add(entity, old);
            _world.Remove<ScoreHistory>(entity);
            ulong version = ++_scoreVersion;
            _world.Add(entity, new ScoreHistory { Value = new ScoreValue { Value = 99 }, Added = version, Written = version });
        }
        int added = 0;
        foreach (ref var chunk in _world.Query(in _scores).GetChunkIterator())
            foreach (ref readonly var score in chunk.GetSpan<ScoreHistory>()[..chunk.Count]) if (score.Added > before) added++;
        AddedCount = added;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            _world.Set(entity, new OldScore { Value = _world.Get<ScoreHistory>(entity).Value, Version = ++_scoreVersion });
            _world.Remove<ScoreHistory>(entity);
        }
        int removed = 0;
        long total = 0;
        foreach (ref var chunk in _world.Query(in _removedScores).GetChunkIterator())
            foreach (ref readonly var score in chunk.GetSpan<OldScore>()[..chunk.Count]) { removed++; total += score.Value.Value; }
        RemovedCount = removed;
        ReadTotal = total;
        _world.Remove<OldScore>(in _removedScores);
        for (int i = 0; i < Count; i += ChangeStride)
        {
            ulong version = ++_scoreVersion;
            _world.Add(_entities[i], new ScoreHistory { Added = version, Written = version });
        }
    }
}

internal sealed partial class DefaultEcsWorld
{
    private DefaultEcs.EntitySet? _changedScores, _addedScores, _removedScores;
    private ulong _scoreVersion;

    private void PrepareChanges(string workload)
    {
        if (_changedScores is not null) return;
        ChangeStride = workload == "changes-1" ? 100 : 4;
        foreach (var entity in _entities)
        {
            ulong version = ++_scoreVersion;
            entity.Set(new ScoreHistory { Added = version, Written = version });
        }
        _changedScores = _world.GetEntities().WhenChanged<ScoreHistory>().AsSet();
        _addedScores = _world.GetEntities().WhenAdded<ScoreHistory>().AsSet();
        _removedScores = _world.GetEntities().With<OldScore>().AsSet();
    }

    internal override void ChangesCycle()
    {
        _changedScores!.Complete();
        _addedScores!.Complete();
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new ScoreHistory { Value = new ScoreValue { Value = 42 }, Added = entity.Get<ScoreHistory>().Added, Written = ++_scoreVersion });
        }
        ChangedCount = _changedScores.Count;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new OldScore { Value = entity.Get<ScoreHistory>().Value, Version = ++_scoreVersion });
            entity.Remove<ScoreHistory>();
            ulong version = ++_scoreVersion;
            entity.Set(new ScoreHistory { Value = new ScoreValue { Value = 99 }, Added = version, Written = version });
        }
        AddedCount = _addedScores.Count;
        for (int i = 0; i < Count; i += ChangeStride)
        {
            var entity = _entities[i];
            entity.Set(new OldScore { Value = entity.Get<ScoreHistory>().Value, Version = ++_scoreVersion });
            entity.Remove<ScoreHistory>();
        }
        long total = 0;
        var scores = _world.GetComponents<OldScore>();
        foreach (ref readonly var entity in _removedScores!.GetEntities()) total += scores[entity].Value.Value;
        RemovedCount = _removedScores.Count;
        ReadTotal = total;
        // Removing a fact updates this set immediately; consume its last member each time.
        while (_removedScores.Count != 0) _removedScores.GetEntities()[^1].Remove<OldScore>();
        for (int i = 0; i < Count; i += ChangeStride)
        {
            ulong version = ++_scoreVersion;
            _entities[i].Set(new ScoreHistory { Added = version, Written = version });
        }
    }
}
