using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

public interface IReferenceRemapper
{
    bool TryMap(Entity source, out Entity mapped);
}

public interface IReferencePatcher<T>
{
    void Remap(ref T value, IReferenceRemapper remapper);
}

internal abstract class ReadReferenceRemapper : IReferenceRemapper
{
    public abstract bool TryMap(Entity source, out Entity mapped);
}

internal readonly struct LoadedEntityMapper(IReferenceRemapper remapper) : IEntityMapper
{
    public Entity Map(Entity source)
    {
        remapper.TryMap(source, out Entity mapped);
        return mapped;
    }
}

internal sealed class PreserveReferenceRemapper : ReadReferenceRemapper
{
    internal static readonly PreserveReferenceRemapper Instance = new();
    public override bool TryMap(Entity source, out Entity mapped) { mapped = source; return true; }
}

internal sealed class ReadIdentityMap
{
    private readonly record struct Entry(int SourceGeneration, Entity Target);
    private readonly Entry[] _entries;
    internal readonly View Current, Progress;

    internal ReadIdentityMap(int[] generations, int[] free, MissingReferenceMode missing)
    {
        _entries = new Entry[generations.Length];
        for (int id = 1; id < generations.Length; id++) _entries[id] = new(generations[id], default);
        foreach (int id in free) _entries[id] = new(-generations[id], default);
        int next = 0;
        for (int id = 1; id < _entries.Length; id++)
            if (_entries[id].SourceGeneration > 0)
                _entries[id] = new(_entries[id].SourceGeneration, new(++next, _entries[id].SourceGeneration));
        for (int i = 0; i < free.Length; i++)
        {
            int id = free[i], generation = -_entries[id].SourceGeneration;
            _entries[id] = new(-generation, new(++next, generation));
            free[i] = next;
        }
        for (int id = 1; id < _entries.Length; id++)
            generations[_entries[id].Target.Id] = _entries[id].Target.Generation;
        Current = new(this, missing, false);
        Progress = new(this, missing, true);
    }

    internal int TargetId(int sourceId) => _entries[sourceId].Target.Id;

    internal sealed class View : ReadReferenceRemapper
    {
        private readonly ReadIdentityMap _map;
        private readonly MissingReferenceMode _missing;
        private readonly bool _progress;

        internal View(ReadIdentityMap map, MissingReferenceMode missing, bool progress)
        {
            _map = map; _missing = missing; _progress = progress;
        }
        public override bool TryMap(Entity source, out Entity mapped)
        {
            if (source == Entity.Null) { mapped = source; return true; }
            if ((uint)(source.Id - 1) < (uint)(_map._entries.Length - 1))
            {
                Entry entry = _map._entries[source.Id];
                if (entry.SourceGeneration > 0 && source.Generation == entry.SourceGeneration)
                { mapped = entry.Target; return true; }
                if (_progress && source.Generation > 0 && source.Generation < Math.Abs(entry.SourceGeneration))
                { mapped = new(entry.Target.Id, source.Generation); return true; }
            }
            mapped = _missing switch
            {
                MissingReferenceMode.KeepOriginal => source,
                MissingReferenceMode.Clear => Entity.Null,
                MissingReferenceMode.Throw => throw new InvalidOperationException($"Missing entity reference remap for {source}."),
                _ => throw new ArgumentOutOfRangeException(nameof(_missing)),
            };
            return true;
        }
    }
}
