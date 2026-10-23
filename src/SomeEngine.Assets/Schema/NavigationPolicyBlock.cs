using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.NavigationPolicyBlock")]
public sealed partial class NavigationPolicyBlock : AssetBlock, IAssetBlockWriter
{
    public const ulong AreaCostsChunkKey = 1;
    public const ulong ActionCostsChunkKey = 2;
    private NavigationPolicy _policy = new();

    public NavigationPolicyBlock() { }
    private NavigationPolicyBlock(NavigationPolicyBlock source, object? syncRoot = null) : base(source, syncRoot)
        => _policy = source._policy;

    public override NavigationPolicyBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationPolicyBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public void SetPolicy(NavigationPolicy policy)
    {
        lock (SyncRoot) { RequireIdle(); _policy = policy; MarkChanged(); }
    }

    /// <summary>Shares the complete policy acquired by this block's explicit read.</summary>
    public NavigationPolicy Create()
    {
        lock (SyncRoot) { RequireIdle(); return _policy; }
    }

    public ValueTask<NavigationPolicy> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create());
    }

    public static async ValueTask<NavigationPolicyBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationPolicyData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> areas = await source.ReadBytesAsync(AreaCostsChunkKey, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> actions = await source.ReadBytesAsync(ActionCostsChunkKey, cancellationToken).ConfigureAwait(false);
        var result = new NavigationPolicyBlock
        {
            _policy = new(NavigationArrayData.Decode<NavigationAreaCost>(areas), NavigationArrayData.Decode<NavigationActionCost>(actions),
                data.DefaultAreaCost, data.AllowedCapabilities, data.HasCrowd ? new NavigationCrowdPolicy
                {
                    Sampling = data.CrowdSampling, FreeSpeed = data.CrowdFreeSpeed, MinimumSpeed = data.CrowdMinimumSpeed,
                    LowDensity = data.CrowdLowDensity, HighDensity = data.CrowdHighDensity,
                    DistanceWeight = data.CrowdDistanceWeight, TimeWeight = data.CrowdTimeWeight,
                    DiscomfortWeight = data.CrowdDiscomfortWeight,
                } : null),
        };
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigationPolicy policy = Create();
        writer.WriteMetadata(BlockTypeId, new NavigationPolicyData
        {
            DefaultAreaCost = policy.DefaultAreaCost, AllowedCapabilities = policy.AllowedCapabilities,
            HasCrowd = policy.Crowd is not null, CrowdSampling = policy.Crowd?.Sampling ?? NavigationCrowdCostSampling.Directional,
            CrowdFreeSpeed = policy.Crowd?.FreeSpeed ?? 0, CrowdMinimumSpeed = policy.Crowd?.MinimumSpeed ?? 0,
            CrowdLowDensity = policy.Crowd?.LowDensity ?? 0, CrowdHighDensity = policy.Crowd?.HighDensity ?? 0,
            CrowdDistanceWeight = policy.Crowd?.DistanceWeight ?? 0, CrowdTimeWeight = policy.Crowd?.TimeWeight ?? 0,
            CrowdDiscomfortWeight = policy.Crowd?.DiscomfortWeight ?? 0,
        });
        writer.WriteChunk(AreaCostsChunkKey, NavigationArrayData.Encode(policy.AreaCosts));
        writer.WriteChunk(ActionCostsChunkKey, NavigationArrayData.Encode(policy.ActionCosts));
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _policy = null!;
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class NavigationPolicyData
{
    public float DefaultAreaCost { get; set; }
    public ulong AllowedCapabilities { get; set; }
    public bool HasCrowd { get; set; }
    public NavigationCrowdCostSampling CrowdSampling { get; set; }
    public float CrowdFreeSpeed { get; set; }
    public float CrowdMinimumSpeed { get; set; }
    public float CrowdLowDensity { get; set; }
    public float CrowdHighDensity { get; set; }
    public float CrowdDistanceWeight { get; set; }
    public float CrowdTimeWeight { get; set; }
    public float CrowdDiscomfortWeight { get; set; }
}
