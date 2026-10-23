using SomeEngine.Job;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct HeadingNavigationFlowRegionSource(int Id, AssetId<NavigationAsset> Navigation,
    NavigationMeshSelection Mesh, Guid NavigationVersion = default);

public readonly record struct HeadingNavigationFlowSource(ReadOnlyMemory<HeadingNavigationFlowRegionSource> Regions,
    AssetId<NavigationAsset> Links, AssetId<NavigationProfileAsset> Profile, AssetId<NavigationPolicyAsset> Policy,
    Guid LinksVersion = default, Guid ProfileVersion = default, Guid PolicyVersion = default);

/// <summary>Typed heading-region sources, ports and shared goals for a completed heading flow.</summary>
[AssetBlock("SomeEngine.Assets.Schema.HeadingNavigationFlowBlock")]
public sealed partial class HeadingNavigationFlowBlock : AssetBlock, IAssetBlockWriter
{
    private HeadingNavigationFlowSource _source;
    private Dictionary<int, ReadOnlyMemory<HeadingNavigationFlowPort>> _ports = [];
    private ReadOnlyMemory<NavigationFlowGoal> _goals;
    private ulong _graphVersion, _goalVersion, _inputVersion;

    public HeadingNavigationFlowBlock() { }
    private HeadingNavigationFlowBlock(HeadingNavigationFlowBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _source = source._source;
        _ports = source._ports;
        _goals = source._goals;
        _graphVersion = source._graphVersion;
        _goalVersion = source._goalVersion;
        _inputVersion = source._inputVersion;
    }

    public override HeadingNavigationFlowBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override HeadingNavigationFlowBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public HeadingNavigationFlowSource Source { get { lock (SyncRoot) { RequireIdle(); return _source; } } }
    public ReadOnlyMemory<NavigationFlowGoal> Goals { get { lock (SyncRoot) { RequireIdle(); return _goals; } } }
    public ReadOnlyMemory<HeadingNavigationFlowPort> GetPorts(int region)
    {
        lock (SyncRoot) { RequireIdle(); return _ports[region]; }
    }

    public void SetFlow(HeadingNavigationFlowSource source, NavigationFlow flow)
    {
        NavigationFlowInput input = flow.Input;
        HeadingNavigationFlowGraph graph = input.HeadingGraph!;
        lock (SyncRoot)
        {
            RequireIdle();
            _source = source;
            _ports = graph.Regions.ToArray().ToDictionary(region => region.Id, region => region.Ports);
            _goals = input.Goals;
            _graphVersion = graph.Costs.Version;
            _goalVersion = input.GoalVersion;
            _inputVersion = input.Version;
            MarkChanged();
        }
    }

    /// <summary>Integrates the saved goals using already acquired shared resources.</summary>
    public NavigationFlow Create(HeadingNavigationFlowGraph graph, NavigationProfile profile, Scheduler? scheduler = null)
    {
        using HeadingNavigationFlowBlock copy = Duplicate();
        return copy.CreateFlow(graph, profile, scheduler);
    }

    /// <summary>Acquires typed region meshes, links, profile and policy, then returns a completed shared field.</summary>
    public async ValueTask<NavigationFlow> CreateAsync(AssetLoader loader, Scheduler? scheduler = null,
        CancellationToken cancellationToken = default)
    {
        using HeadingNavigationFlowBlock copy = Duplicate();
        HeadingNavigationFlowSource source = copy._source;
        using NavigationProfileAsset profileAsset = await NavigationProfileAsset.OpenAsync(loader, source.Profile, cancellationToken).ConfigureAwait(false);
        using NavigationPolicyAsset policyAsset = await NavigationPolicyAsset.OpenAsync(loader, source.Policy, cancellationToken).ConfigureAwait(false);
        using NavigationAsset linksAsset = await NavigationAsset.OpenAsync(loader, source.Links, cancellationToken).ConfigureAwait(false);
        NavigationProfile profile = (await profileAsset.GetBlockAsync<NavigationProfileBlock>(cancellationToken).ConfigureAwait(false)).Create();
        NavigationPolicy policy = (await policyAsset.GetBlockAsync<NavigationPolicyBlock>(cancellationToken).ConfigureAwait(false)).Create();
        NavigationLinkSet links = (await linksAsset.GetBlockAsync<NavigationLinkBlock>(cancellationToken).ConfigureAwait(false)).Create();
        bool sameInputs = source.ProfileVersion == profileAsset.SourceVersion && source.PolicyVersion == policyAsset.SourceVersion &&
            source.LinksVersion == linksAsset.SourceVersion;
        var regions = new HeadingNavigationFlowRegion[source.Regions.Length];
        for (int index = 0; index < regions.Length; index++)
        {
            HeadingNavigationFlowRegionSource region = source.Regions.Span[index];
            using NavigationAsset asset = await NavigationAsset.OpenAsync(loader, region.Navigation, cancellationToken).ConfigureAwait(false);
            HeadingNavigationMesh mesh = await (await asset.GetBlockAsync<HeadingNavigationMeshBlock>(cancellationToken).ConfigureAwait(false))
                .CreateAsync(region.Mesh, cancellationToken).ConfigureAwait(false);
            regions[index] = new(region.Id, mesh, copy._ports[region.Id]);
            sameInputs &= region.NavigationVersion == asset.SourceVersion;
        }
        HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(regions, links, profile, policy,
            sameInputs ? copy._graphVersion : copy._graphVersion + 1, scheduler: scheduler).Complete().Graph;
        return copy.CreateFlow(graph, profile, scheduler);
    }

    private NavigationFlow CreateFlow(HeadingNavigationFlowGraph graph, NavigationProfile profile, Scheduler? scheduler)
    {
        ulong version = graph.Costs.Version == _graphVersion ? _inputVersion : _inputVersion + 1;
        var flow = new NavigationFlow(new NavigationFlowInput(graph.Costs, profile, _goals, _goalVersion, version, graph));
        new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler).Schedule().Complete();
        return flow;
    }

    public override IEnumerable<Guid> References()
    {
        HeadingNavigationFlowSource source = Source;
        var references = new List<Guid> { source.Profile.Value, source.Policy.Value, source.Links.Value };
        foreach (HeadingNavigationFlowRegionSource region in source.Regions.Span)
        {
            references.Add(region.Navigation.Value);
            references.Add(region.Mesh.Profile.Value);
        }
        return references.Distinct();
    }

    public static async ValueTask<HeadingNavigationFlowBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new HeadingNavigationFlowData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        var result = new HeadingNavigationFlowBlock
        {
            _goals = NavigationArrayData.Decode<NavigationFlowGoal>(await source.ReadBytesAsync(1, cancellationToken).ConfigureAwait(false)),
            _graphVersion = data.GraphVersion, _goalVersion = data.GoalVersion, _inputVersion = data.InputVersion,
        };
        var regions = new HeadingNavigationFlowRegionSource[data.Regions.Count];
        for (int index = 0; index < regions.Length; index++)
        {
            HeadingNavigationFlowRegionData region = data.Regions[index];
            regions[index] = new(region.Id, region.Navigation, new(region.Profile, region.Variant), region.NavigationVersion);
            result._ports.Add(region.Id, NavigationArrayData.Decode<HeadingNavigationFlowPort>(
                await source.ReadBytesAsync(region.PortsChunk, cancellationToken).ConfigureAwait(false)));
        }
        result._source = new(regions, data.Links, data.Profile, data.Policy, data.LinksVersion, data.ProfileVersion, data.PolicyVersion);
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using HeadingNavigationFlowBlock copy = Duplicate();
        HeadingNavigationFlowSource source = copy._source;
        var data = new HeadingNavigationFlowData
        {
            Links = source.Links, Profile = source.Profile, Policy = source.Policy,
            LinksVersion = source.LinksVersion, ProfileVersion = source.ProfileVersion, PolicyVersion = source.PolicyVersion,
            GraphVersion = copy._graphVersion, GoalVersion = copy._goalVersion, InputVersion = copy._inputVersion,
        };
        ulong next = 2;
        foreach (HeadingNavigationFlowRegionSource region in source.Regions.Span)
            data.Regions.Add(new()
            {
                Id = region.Id, Navigation = region.Navigation, Profile = region.Mesh.Profile, Variant = region.Mesh.Variant,
                NavigationVersion = region.NavigationVersion, PortsChunk = next++,
            });
        writer.WriteMetadata(BlockTypeId, data);
        writer.WriteChunk(1, NavigationArrayData.Encode(copy._goals));
        foreach (HeadingNavigationFlowRegionData region in data.Regions)
            writer.WriteChunk(region.PortsChunk, NavigationArrayData.Encode(copy._ports[region.Id]));
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _source = default;
        _ports = [];
        _goals = default;
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class HeadingNavigationFlowData
{
    public AssetId<NavigationAsset> Links { get; set; }
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public AssetId<NavigationPolicyAsset> Policy { get; set; }
    public Guid LinksVersion { get; set; }
    public Guid ProfileVersion { get; set; }
    public Guid PolicyVersion { get; set; }
    public ulong GraphVersion { get; set; }
    public ulong GoalVersion { get; set; }
    public ulong InputVersion { get; set; }
    public List<HeadingNavigationFlowRegionData> Regions { get; set; } = [];
}

[BinarySerializable]
internal sealed partial class HeadingNavigationFlowRegionData
{
    public int Id { get; set; }
    public AssetId<NavigationAsset> Navigation { get; set; }
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public string Variant { get; set; } = "";
    public Guid NavigationVersion { get; set; }
    public ulong PortsChunk { get; set; }
}
