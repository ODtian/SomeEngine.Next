using SomeEngine.ECS.Systems;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Render.Systems;

/// <summary>Collects independent source ranges before merging at the existing extraction boundary.</summary>
public interface IRenderExtractionRangeSystem : IRenderExtractionSystem
{
    IRenderExtractionCollector CreateRangeCollector();

    bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context);
}

/// <summary>Holds one range's values until its extraction system merges them.</summary>
public interface IRenderExtractionCollector : IDisposable
{
    void Reset();

    void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection);
}

public sealed partial class RenderExtractionSystems
{
    private readonly List<RangeCollection> _rangeCollections = [];
    private readonly HashSet<Entity> _fullCollectionSources = [];

    private sealed class RangeCollection : IDisposable
    {
        internal readonly IRenderExtractionCollector?[] Collectors;
        internal bool Selected;

        internal RangeCollection(List<IRenderExtractionSystem> systems)
        {
            Collectors = new IRenderExtractionCollector?[systems.Count];
            try
            {
                for (int index = 0; index < systems.Count; index++)
                    if (systems[index] is IRenderExtractionRangeSystem system)
                        Collectors[index] = system.CreateRangeCollector();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Reset()
        {
            Selected = false;
            foreach (IRenderExtractionCollector? collector in Collectors) collector?.Reset();
        }

        public void Dispose()
        {
            foreach (IRenderExtractionCollector? collector in Collectors) collector?.Dispose();
        }
    }

    private readonly struct CollectRangesJob(RenderExtractionSystems systems) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet)
        {
            RangeCollection collection = systems._rangeCollections[context.PacketIndex];
            bool full = systems._candidateFullReconciliation;
            if (full && !systems._fullCollectionSources.Contains(packet.Entities[0])) return;
            collection.Selected = true;
            foreach (IRenderExtractionCollector? collector in collection.Collectors)
                collector?.Collect(packet, context.LastSystemVersion, full);
        }
    }

    private void CollectRanges(scoped QueryCursor query)
    {
        if (!_systems.Exists(static system => system is IRenderExtractionRangeSystem))
        {
            if (_candidateFullReconciliation) Collect(query);
            else CollectDelta(query);
            return;
        }

        using ReadOnlyQueryPacketPlan ranges = ReadOnlyQueryPacketJobs.CreatePlan(query);
        while (_rangeCollections.Count < ranges.PacketCount) _rangeCollections.Add(new(_systems));
        _fullCollectionSources.Clear();
        if (_candidateFullReconciliation)
            foreach (QueryChunkView chunk in query.ChangedChunks(_candidateSourceChanges!))
            {
                _context.IncludeSources(chunk.Entities);
                foreach (Entity source in chunk.Entities) _fullCollectionSources.Add(source);
            }

        try
        {
            ranges.ExecuteParallel(new CollectRangesJob(this));
            int rangeIndex = 0;
            foreach (QueryChunkView chunk in query.Chunks)
            {
                if (chunk.Count == 0) continue;
                RangeCollection collection = _rangeCollections[rangeIndex++];
                if (!collection.Selected) continue;
                for (int index = 0; index < _systems.Count; index++)
                {
                    IRenderExtractionSystem system = _systems[index];
                    if (system is IRenderExtractionRangeSystem rangeSystem)
                    {
                        if (rangeSystem.MergeRangeCollector(collection.Collectors[index]!, _context))
                            _changedSystems.Add(system);
                    }
                    else if (_candidateFullReconciliation) system.Collect(chunk);
                    else if (system.CollectChanges(chunk, _context)) _changedSystems.Add(system);
                }
            }
        }
        finally
        {
            for (int index = 0; index < ranges.PacketCount; index++) _rangeCollections[index].Reset();
            _fullCollectionSources.Clear();
        }
    }
}
