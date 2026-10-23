using SomeEngine.Navigation;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A profile asset's shared motion primitives, controls, timed trajectories and swept body geometry.</summary>
[AssetBlock("SomeEngine.Assets.Schema.NavigationMotionBlock")]
public sealed partial class NavigationMotionBlock : AssetBlock, IAssetBlockWriter
{
    public const ulong HeadingsChunkKey = 1;
    public const ulong PrimitivesChunkKey = 2;
    public const ulong ControlsChunkKey = 3;
    public const ulong SamplesChunkKey = 4;
    public const ulong SweptGeometryChunkKey = 5;
    private NavigationMotionSet? _motions;

    public NavigationMotionBlock() { }
    private NavigationMotionBlock(NavigationMotionBlock source, object? syncRoot = null) : base(source, syncRoot) => _motions = source._motions;

    public override NavigationMotionBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override NavigationMotionBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    public void SetMotionSet(NavigationMotionSet motions)
    {
        lock (SyncRoot) { RequireIdle(); _motions = motions; MarkChanged(); }
    }

    /// <summary>Shares the completed arrays acquired by this block's explicit read.</summary>
    public NavigationMotionSet Create()
    {
        lock (SyncRoot)
        {
            RequireIdle();
            return _motions ?? throw new InvalidOperationException("Supply navigation motion content before creation.");
        }
    }

    public ValueTask<NavigationMotionSet> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Create());
    }

    public static async ValueTask<NavigationMotionBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var data = new NavigationMotionData();
        await source.ReadIntoAsync(data, cancellationToken).ConfigureAwait(false);
        float[] headings = NavigationArrayData.Decode<float>(await source.ReadBytesAsync(HeadingsChunkKey, cancellationToken).ConfigureAwait(false));
        NavigationMotionPrimitive[] primitives = NavigationArrayData.Decode<NavigationMotionPrimitive>(
            await source.ReadBytesAsync(PrimitivesChunkKey, cancellationToken).ConfigureAwait(false));
        NavigationVehicleControl[] controls = NavigationArrayData.Decode<NavigationVehicleControl>(
            await source.ReadBytesAsync(ControlsChunkKey, cancellationToken).ConfigureAwait(false));
        NavigationMotionSample[] samples = NavigationArrayData.Decode<NavigationMotionSample>(
            await source.ReadBytesAsync(SamplesChunkKey, cancellationToken).ConfigureAwait(false));
        NavigationMotionSweepTriangle[] swept = NavigationArrayData.Decode<NavigationMotionSweepTriangle>(
            await source.ReadBytesAsync(SweptGeometryChunkKey, cancellationToken).ConfigureAwait(false));
        var result = new NavigationMotionBlock
        {
            _motions = new(new(data.MinimumTurningRadius, data.AllowReverse), data.Resolution,
                headings, primitives, controls, samples, swept),
        };
        result.AttachSource(source);
        return result;
    }

    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NavigationMotionSet motions = Create();
        writer.WriteMetadata(BlockTypeId, new NavigationMotionData
        {
            MinimumTurningRadius = motions.Model.MinimumTurningRadius,
            AllowReverse = motions.Model.AllowReverse,
            Resolution = motions.Resolution,
        });
        writer.WriteChunk(HeadingsChunkKey, NavigationArrayData.Encode(motions.Headings));
        writer.WriteChunk(PrimitivesChunkKey, NavigationArrayData.Encode(motions.Primitives));
        writer.WriteChunk(ControlsChunkKey, NavigationArrayData.Encode(motions.Controls));
        writer.WriteChunk(SamplesChunkKey, NavigationArrayData.Encode(motions.Samples));
        writer.WriteChunk(SweptGeometryChunkKey, NavigationArrayData.Encode(motions.SweptGeometry));
        return ValueTask.CompletedTask;
    }

    protected override void ReleaseContent()
    {
        _motions = null;
        base.ReleaseContent();
    }
}

[BinarySerializable]
internal sealed partial class NavigationMotionData
{
    public float MinimumTurningRadius { get; set; }
    public bool AllowReverse { get; set; }
    public float Resolution { get; set; }
}
