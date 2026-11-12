using SomeEngine.ECS;
using SomeEngine.Job;

namespace SomeEngine.Render.Systems;

public interface IRenderExtractionRangeSystem : IRenderExtractionSystem
{
    IRenderExtractionCollector CreateRangeCollector();
    bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context);
}

public interface IRenderExtractionCollector : IDisposable
{
    void Reset();
    void Collect(QueryChunk chunk, ulong since, bool fullCollection);
}

public sealed partial class RenderExtractionSystems
{
    private RangeCollection[] _rangeCollections = [];

    private sealed class RangeCollection : IDisposable
    {
        internal readonly IRenderExtractionCollector?[] Collectors;
        internal QueryChunk Chunk;
        internal bool FullCollection;

        internal RangeCollection(List<IRenderExtractionSystem> systems)
        {
            Collectors = new IRenderExtractionCollector?[systems.Count];
            try
            {
                for (int i = 0; i < systems.Count; i++)
                    if (systems[i] is IRenderExtractionRangeSystem system) Collectors[i] = system.CreateRangeCollector();
            }
            catch { Dispose(); throw; }
        }
        internal void Reset()
        {
            Chunk = default; FullCollection = false;
            foreach (IRenderExtractionCollector? collector in Collectors) collector?.Reset();
        }
        public void Dispose()
        {
            foreach (IRenderExtractionCollector? collector in Collectors) collector?.Dispose();
        }
    }

    private readonly struct CollectRangesJob(RenderExtractionSystems systems) : IRangeWork<RangeCollection>
    {
        public void Execute(in RangeCollection collection)
        {
            foreach (IRenderExtractionCollector? collector in collection.Collectors)
                collector?.Collect(collection.Chunk, systems._lastSystemVersion, collection.FullCollection);
        }
    }

    private bool IncludesChangedSource(QueryChunk chunk)
    {
        if (_mainWorld is null) return true;
        foreach (var entity in chunk.Entities)
            if (_sourceChanges.Contains(entity)) return true;
        return false;
    }

    private void CollectRanges(scoped QueryScope source, WorldExecution? execution)
    {
        if (!_systems.Exists(static system => system is IRenderExtractionRangeSystem))
        {
            foreach (QueryChunk chunk in source)
            {
                if (_candidateFullReconciliation && IncludesChangedSource(chunk))
                {
                    _context.IncludeSources(chunk.Entities);
                    foreach (IRenderExtractionSystem system in _systems) system.Collect(chunk);
                }
                else
                    foreach (IRenderExtractionSystem system in _systems)
                        if (system.CollectChanges(chunk, _lastSystemVersion, _context)) _changedSystems.Add(system);
            }
            return;
        }
        int count = 0;
        foreach (QueryChunk chunk in source)
        {
            if (count == _rangeCollections.Length) Array.Resize(ref _rangeCollections, Math.Max(4, count * 2));
            RangeCollection collection = _rangeCollections[count] ??= new(_systems);
            collection.Chunk = chunk;
            collection.FullCollection = _candidateFullReconciliation && IncludesChangedSource(chunk);
            if (collection.FullCollection) _context.IncludeSources(chunk.Entities);
            count++;
        }
        try
        {
            var work = new CollectRangesJob(this);
            if (execution is not null) execution.ExecuteRanges(_rangeCollections, count, work);
            else for (int range = 0; range < count; range++) work.Execute(in _rangeCollections[range]);
            for (int range = 0; range < count; range++)
            {
                RangeCollection collection = _rangeCollections[range];
                for (int i = 0; i < _systems.Count; i++)
                {
                    IRenderExtractionSystem system = _systems[i];
                    if (system is IRenderExtractionRangeSystem ranged)
                    {
                        if (ranged.MergeRangeCollector(collection.Collectors[i]!, _context)) _changedSystems.Add(system);
                    }
                    else if (collection.FullCollection) system.Collect(collection.Chunk);
                    else if (system.CollectChanges(collection.Chunk, _lastSystemVersion, _context)) _changedSystems.Add(system);
                }
            }
        }
        finally { for (int range = 0; range < count; range++) _rangeCollections[range].Reset(); }
    }
}
