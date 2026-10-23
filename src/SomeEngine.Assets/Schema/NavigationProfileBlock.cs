using System.Numerics;
using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[AssetBlock("SomeEngine.Assets.Schema.NavigationProfileBlock")]
public sealed partial class NavigationProfileBlock : AssetBlock, IAssetBlockWriter
{
    public const ulong FootprintChunkKey = 1;
    private NavigationProfile? _profile;

    public NavigationProfileBlock() { }
    private NavigationProfileBlock(NavigationProfileBlock source, object? syncRoot = null) : base(source, syncRoot)
        => _profile = source._profile;

    public override NavigationProfileBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationProfileBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public void SetProfile(NavigationProfile profile)
    {
        lock (SyncRoot) { RequireIdle(); _profile = profile; MarkChanged(); }
    }

    /// <summary>Shares the complete profile acquired by this block's explicit read.</summary>
    public NavigationProfile Create()
    {
        lock (SyncRoot)
        {
            RequireIdle();
            return _profile ?? throw new InvalidOperationException("Supply navigation profile content before creation.");
        }
    }

    public ValueTask<NavigationProfile> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create());
    }

    public static async ValueTask<NavigationProfileBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationProfileData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> footprint = await source.ReadBytesAsync(FootprintChunkKey, cancellationToken).ConfigureAwait(false);
        var result = new NavigationProfileBlock();
        result._profile = new(NavigationArrayData.Decode<Vector2>(footprint), data.Height, data.MaximumSlopeRadians,
            data.MaximumStepHeight, data.MaximumSpeed, data.MaximumAcceleration, data.MaximumAngularSpeed, data.Capabilities,
            data.HasVehicleModel ? new NavigationVehicleModel(data.MinimumTurningRadius, data.AllowReverse) : null);
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigationProfile profile = Create();
        writer.WriteMetadata(BlockTypeId, new NavigationProfileData
        {
            Height = profile.Height, MaximumSlopeRadians = profile.MaximumSlopeRadians,
            MaximumStepHeight = profile.MaximumStepHeight, MaximumSpeed = profile.MaximumSpeed,
            MaximumAcceleration = profile.MaximumAcceleration, MaximumAngularSpeed = profile.MaximumAngularSpeed,
            Capabilities = profile.Capabilities,
            HasVehicleModel = profile.VehicleModel.HasValue,
            MinimumTurningRadius = profile.VehicleModel?.MinimumTurningRadius ?? 0,
            AllowReverse = profile.VehicleModel?.AllowReverse ?? false,
        });
        writer.WriteChunk(FootprintChunkKey, NavigationArrayData.Encode(profile.Footprint));
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _profile = null;
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class NavigationProfileData
{
    public float Height { get; set; }
    public float MaximumSlopeRadians { get; set; }
    public float MaximumStepHeight { get; set; }
    public float MaximumSpeed { get; set; }
    public float MaximumAcceleration { get; set; }
    public float MaximumAngularSpeed { get; set; }
    public ulong Capabilities { get; set; }
    public bool HasVehicleModel { get; set; }
    public float MinimumTurningRadius { get; set; }
    public bool AllowReverse { get; set; }
}
