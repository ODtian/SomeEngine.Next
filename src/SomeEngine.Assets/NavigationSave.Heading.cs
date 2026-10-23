using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

public sealed partial class NavigationSaveResources
{
    public void RegisterHeadingFlow(NavigationFlow value, AssetId<NavigationFlowAsset> asset) => Sources[value] = new SavedFlowAsset(asset, true);
}

internal readonly record struct SavedHeadingRegion(int Id, int Mesh, HeadingNavigationFlowPort[] Ports);
internal sealed record SavedHeadingGraph(int Profile, int Policy, int Links, ulong Version, SavedHeadingRegion[] Regions) : NavigationSavedResource;
internal sealed record SavedHeadingCosts(int Graph) : NavigationSavedResource;
internal sealed record SavedHeadingGrid(int Graph) : NavigationSavedResource;

internal sealed partial class NavigationSaveResourceCapture
{
    private int CaptureHeadingGraph(HeadingNavigationFlowGraph graph, NavigationProfile profile)
    {
        if (_indices.TryGetValue(graph, out int existing)) return existing;
        int index = Records.Count;
        _indices.Add(graph, index);
        Records.Add(null!);
        _derived[graph.Costs] = new SavedHeadingCosts(index);
        _derived[graph.Costs.Grid] = new SavedHeadingGrid(index);
        var regions = graph.Regions.ToArray().Select(region => new SavedHeadingRegion(region.Id, Add(region.Mesh), region.Ports.ToArray())).ToArray();
        Records[index] = new SavedHeadingGraph(Add(profile), Add(graph.Costs.Policy), Add(graph.Links), graph.Costs.Version, regions);
        return index;
    }
}

internal sealed partial class NavigationSaveResourceReader
{
    private async Task<HeadingNavigationFlowGraph> RestoreHeadingGraph(SavedHeadingGraph saved)
    {
        var regions = new HeadingNavigationFlowRegion[saved.Regions.Length];
        for (int index = 0; index < regions.Length; index++)
        {
            SavedHeadingRegion region = saved.Regions[index];
            regions[index] = new(region.Id, (HeadingNavigationMesh)await Get(region.Mesh).ConfigureAwait(false), region.Ports);
        }
        return HeadingNavigationFlowBuilder.Schedule(regions, (NavigationLinkSet)await Get(saved.Links).ConfigureAwait(false),
            (NavigationProfile)await Get(saved.Profile).ConfigureAwait(false), (NavigationPolicy)await Get(saved.Policy).ConfigureAwait(false),
            saved.Version, scheduler: scheduler).Complete().Graph;
    }
}

internal readonly partial struct NavigationSaveCodec
{
    private static void WriteHeadingResource(ref DataWriter writer, NavigationSavedResource resource)
    {
        switch (resource)
        {
            case SavedHeadingGraph graph:
                writer.WriteByte(15); writer.WriteInt32(graph.Profile); writer.WriteInt32(graph.Policy); writer.WriteInt32(graph.Links); writer.WriteUInt64(graph.Version);
                writer.WriteInt32(graph.Regions.Length);
                foreach (SavedHeadingRegion region in graph.Regions)
                {
                    writer.WriteInt32(region.Id); writer.WriteInt32(region.Mesh); writer.WriteInt32(region.Ports.Length);
                    foreach (HeadingNavigationFlowPort port in region.Ports) { writer.WriteInt32(port.Id); WriteVector(ref writer, port.Position); }
                }
                break;
            case SavedHeadingCosts costs: writer.WriteByte(16); writer.WriteInt32(costs.Graph); break;
            case SavedHeadingGrid grid: writer.WriteByte(17); writer.WriteInt32(grid.Graph); break;
            default: throw new InvalidOperationException($"Unknown navigation save resource {resource.GetType().Name}.");
        }
    }

    private static NavigationSavedResource ReadHeadingResource(byte kind, ref DataReader reader)
    {
        switch (kind)
        {
            case 15:
                int profile = reader.ReadInt32(), policy = reader.ReadInt32(), links = reader.ReadInt32();
                ulong version = reader.ReadUInt64();
                var regions = new SavedHeadingRegion[reader.ReadInt32()];
                for (int index = 0; index < regions.Length; index++)
                {
                    int id = reader.ReadInt32(), mesh = reader.ReadInt32();
                    var ports = new HeadingNavigationFlowPort[reader.ReadInt32()];
                    for (int port = 0; port < ports.Length; port++) ports[port] = new(reader.ReadInt32(), ReadVector(ref reader));
                    regions[index] = new(id, mesh, ports);
                }
                return new SavedHeadingGraph(profile, policy, links, version, regions);
            case 16: return new SavedHeadingCosts(reader.ReadInt32());
            case 17: return new SavedHeadingGrid(reader.ReadInt32());
            default: throw new InvalidDataException($"Unknown navigation save resource kind {kind}.");
        }
    }
}
