using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public readonly record struct NavigationFlowSource(
    AssetId<NavigationAsset> Navigation, AssetId<NavigationProfileAsset> Profile,
    AssetId<NavigationPolicyAsset> Policy, string GridVariant,
    Guid NavigationVersion = default, Guid ProfileVersion = default, Guid PolicyVersion = default)
{
    public static NavigationFlowSource FromAssets(NavigationAsset navigation, NavigationProfileAsset profile,
        NavigationPolicyAsset policy, string gridVariant) =>
        new(navigation.Reference, profile.Reference, policy.Reference, gridVariant,
            navigation.SourceVersion, profile.SourceVersion, policy.SourceVersion);
}

/// <summary>Shared goals and typed input references, with an optional completed field.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationFlowBlock")]
public sealed partial class NavigationFlowBlock : AssetBlock, IAssetBlockWriter
{
    private NavigationFlowSource _source;
    private ReadOnlyMemory<NavigationFlowGoal> _goals;
    private NavigationFlowArrays? _field;
    private ulong _gridVersion, _costVersion, _goalVersion, _inputVersion;

    public NavigationFlowBlock() { }
    private NavigationFlowBlock(NavigationFlowBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _source = source._source;
        _goals = source._goals;
        _field = source._field;
        _gridVersion = source._gridVersion;
        _costVersion = source._costVersion;
        _goalVersion = source._goalVersion;
        _inputVersion = source._inputVersion;
    }

    public override NavigationFlowBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationFlowBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public NavigationFlowSource Source { get { lock (SyncRoot) { RequireIdle(); return _source; } } }
    public ReadOnlyMemory<NavigationFlowGoal> Goals { get { lock (SyncRoot) { RequireIdle(); return _goals; } } }
    public bool HasPrecomputedField { get { lock (SyncRoot) { RequireIdle(); return _field is not null; } } }

    public void SetFlow(NavigationFlowSource source, NavigationFlow flow, bool includePrecomputed = false)
    {
        NavigationFlowInput input = flow.Input;
        NavigationFlowField? field = flow.Field;
        lock (SyncRoot)
        {
            RequireIdle();
            _source = source;
            _goals = input.Goals;
            _gridVersion = input.Costs.Grid.ContentVersion;
            _costVersion = input.Costs.Version;
            _goalVersion = input.GoalVersion;
            _inputVersion = input.Version;
            _field = includePrecomputed && ReferenceEquals(field?.Input, input)
                ? new(field!.CumulativeCosts, field.SuccessorEdges, field.Directions, field.GoalIndices, field.Dependencies, field.Solver, field.Sampling)
                : null;
            MarkChanged();
        }
    }

    public ValueTask<NavigationFlow> CreateAsync(AssetLoader loader, Scheduler? scheduler = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync<ReverseDijkstraFlow, ReverseDijkstraFlowState>(loader, scheduler, cancellationToken);

    /// <summary>Explicitly acquires grid, profile and policy before returning a synchronously readable field.</summary>
    public async ValueTask<NavigationFlow> CreateAsync<TSolver, TState>(AssetLoader loader, Scheduler? scheduler = null,
        CancellationToken cancellationToken = default)
        where TSolver : struct, INavigationFlowSolver<TState>
        where TState : NavigationFlowBuildState
    {
        using NavigationFlowBlock copy = Duplicate();
        NavigationFlowSource source = copy._source;
        using NavigationProfileAsset profileAsset = await NavigationProfileAsset.OpenAsync(loader, source.Profile, cancellationToken).ConfigureAwait(false);
        using NavigationPolicyAsset policyAsset = await NavigationPolicyAsset.OpenAsync(loader, source.Policy, cancellationToken).ConfigureAwait(false);
        using NavigationAsset navigationAsset = await NavigationAsset.OpenAsync(loader, source.Navigation, cancellationToken).ConfigureAwait(false);
        NavigationProfile profile = (await profileAsset.GetBlockAsync<NavigationProfileBlock>(cancellationToken).ConfigureAwait(false)).Create();
        NavigationPolicy policy = (await policyAsset.GetBlockAsync<NavigationPolicyBlock>(cancellationToken).ConfigureAwait(false)).Create();
        NavigationGrid grid = (await navigationAsset.GetBlockAsync<NavigationGridBlock>(cancellationToken).ConfigureAwait(false))
            .Create(new(source.Profile, source.GridVariant));
        bool sameInputs = source.NavigationVersion == navigationAsset.SourceVersion &&
            source.ProfileVersion == profileAsset.SourceVersion && source.PolicyVersion == policyAsset.SourceVersion &&
            copy._gridVersion == grid.ContentVersion;
        NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy,
            sameInputs ? copy._costVersion : copy._costVersion + 1, scheduler).Complete().Costs;
        var input = new NavigationFlowInput(costs, profile, copy._goals, copy._goalVersion,
            sameInputs ? copy._inputVersion : copy._inputVersion + 1);
        if (sameInputs && copy._field is { } saved && saved.Solver == TSolver.Id)
            return new(new NavigationFlowField(input, saved.Costs, saved.Successors, saved.Directions,
                saved.GoalIndices, saved.Dependencies, saved.Solver, saved.Sampling));
        var flow = new NavigationFlow(input);
        new NavigationFlowSystem<TSolver, TState>(flow, scheduler).Schedule().Complete();
        return flow;
    }

    public override IEnumerable<Guid> References()
    {
        NavigationFlowSource source = Source;
        return [source.Navigation.Value, source.Profile.Value, source.Policy.Value];
    }

    public static async ValueTask<NavigationFlowBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationFlowData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> goals = await source.ReadBytesAsync(1, cancellationToken).ConfigureAwait(false);
        var result = new NavigationFlowBlock
        {
            _source = new(data.Navigation, data.Profile, data.Policy, data.GridVariant,
                data.NavigationVersion, data.ProfileVersion, data.PolicyVersion),
            _goals = NavigationArrayData.Decode<NavigationFlowGoal>(goals),
            _gridVersion = data.GridVersion, _costVersion = data.CostVersion,
            _goalVersion = data.GoalVersion, _inputVersion = data.InputVersion,
        };
        if (data.HasField)
        {
            ReadOnlyMemory<byte> costs = await source.ReadBytesAsync(2, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> successors = await source.ReadBytesAsync(3, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> directions = await source.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> goalIndices = await source.ReadBytesAsync(5, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> dependencies = await source.ReadBytesAsync(6, cancellationToken).ConfigureAwait(false);
            result._field = new(NavigationArrayData.Decode<float>(costs), NavigationArrayData.Decode<int>(successors),
                NavigationArrayData.Decode<Vector3>(directions), NavigationArrayData.Decode<int>(goalIndices),
                NavigationArrayData.Decode<NavigationFlowDependency>(dependencies), data.Solver, data.Sampling);
        }
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using NavigationFlowBlock copy = Duplicate();
        NavigationFlowSource source = copy._source;
        writer.WriteMetadata(BlockTypeId, new NavigationFlowData
        {
            Navigation = source.Navigation, Profile = source.Profile, Policy = source.Policy, GridVariant = source.GridVariant,
            NavigationVersion = source.NavigationVersion, ProfileVersion = source.ProfileVersion, PolicyVersion = source.PolicyVersion,
            GridVersion = copy._gridVersion, CostVersion = copy._costVersion,
            GoalVersion = copy._goalVersion, InputVersion = copy._inputVersion,
            HasField = copy._field is not null, Solver = copy._field?.Solver ?? "", Sampling = copy._field?.Sampling ?? NavigationFlowSampling.Successor,
        });
        writer.WriteChunk(1, NavigationArrayData.Encode(copy._goals));
        if (copy._field is { } field)
        {
            writer.WriteChunk(2, NavigationArrayData.Encode(field.Costs));
            writer.WriteChunk(3, NavigationArrayData.Encode(field.Successors));
            writer.WriteChunk(4, NavigationArrayData.Encode(field.Directions));
            writer.WriteChunk(5, NavigationArrayData.Encode(field.GoalIndices));
            writer.WriteChunk(6, NavigationArrayData.Encode(field.Dependencies));
        }
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _goals = default;
        _field = null;
        base.ReleaseContent();
    }

    private sealed record NavigationFlowArrays(ReadOnlyMemory<float> Costs, ReadOnlyMemory<int> Successors,
        ReadOnlyMemory<Vector3> Directions, ReadOnlyMemory<int> GoalIndices,
        ReadOnlyMemory<NavigationFlowDependency> Dependencies, string Solver, NavigationFlowSampling Sampling);
}

[BinarySerializable]
internal sealed partial class NavigationFlowData
{
    public AssetId<NavigationAsset> Navigation { get; set; }
    public AssetId<NavigationProfileAsset> Profile { get; set; }
    public AssetId<NavigationPolicyAsset> Policy { get; set; }
    public string GridVariant { get; set; } = "";
    public Guid NavigationVersion { get; set; }
    public Guid ProfileVersion { get; set; }
    public Guid PolicyVersion { get; set; }
    public ulong GridVersion { get; set; }
    public ulong CostVersion { get; set; }
    public ulong GoalVersion { get; set; }
    public ulong InputVersion { get; set; }
    public bool HasField { get; set; }
    public string Solver { get; set; } = "";
    public NavigationFlowSampling Sampling { get; set; }
}
