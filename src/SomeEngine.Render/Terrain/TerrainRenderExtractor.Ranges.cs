using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Terrain;

public sealed partial class TerrainRenderExtractor : IRenderExtractionRangeSystem
{
    public IRenderExtractionCollector CreateRangeCollector() => new TerrainSources();

    public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context) =>
        MergeInputs((TerrainSources)collector);

    private bool MergeInputs(TerrainSources values)
    {
        foreach (var value in values.Values)
        {
            _sources[value.Source] = value.Terrain;
            _collected.Add(value.Source);
        }
        return values.Values.Count != 0;
    }

    private sealed class TerrainSources : IRenderExtractionCollector
    {
        internal readonly List<(Entity Source, Resources.Terrain Terrain)> Values = [];
        public void Reset() => Values.Clear();
        public void Dispose() => Reset();

        public void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
        {
            if (!packet.TryRead<TerrainSource>(out var terrains)
                || !fullCollection && !packet.ChangedSince<TerrainSource>(lastSystemVersion)) return;
            for (int row = 0; row < packet.Count; row++) Values.Add((packet.Entities[row], terrains[row].Terrain));
        }
    }
}
