using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>The persistent exported network and normalization data for a ready flow model.</summary>
[AssetBlock("SomeEngine.Animation.Pose.FlowMatchingBlock"), BinarySerializable]
public sealed partial class FlowMatchingBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<FlowMatchingBlock>, IBinaryObjectReader<FlowMatchingBlock>
{
    private AssetId<SkeletonAsset> _skeleton;
    private FlowMatchingModel _model = null!;
    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.FlowMatchingBlock");
    public static ulong SchemaFingerprint => 0x464C4F574D4F4401UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public AssetId<SkeletonAsset> Skeleton => _skeleton;

    public FlowMatchingBlock(AssetId<SkeletonAsset> skeleton, FlowMatchingModel model) => (_skeleton, _model) = (skeleton, model);
    private FlowMatchingBlock() { }
    private FlowMatchingBlock(FlowMatchingBlock source, object? syncRoot = null) : base(source, syncRoot)
        => (_skeleton, _model) = (source._skeleton, source._model);
    public override FlowMatchingBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override FlowMatchingBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public override IEnumerable<Guid> References() { if (!_skeleton.IsEmpty) yield return _skeleton.Value; }
    public FlowMatchingModel CreateModel() { lock (SyncRoot) { RequireIdle(); return _model; } }

    public static void Write(ref BinaryDataWriter writer, FlowMatchingBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteGuid(value._skeleton.Value);
            writer.WriteString(value._model.WriteJson());
        }
    }

    public static FlowMatchingBlock Read(ref BinaryDataReader reader)
    {
        var result = new FlowMatchingBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, FlowMatchingBlock target)
    {
        var skeleton = new AssetId<SkeletonAsset>(reader.ReadGuid());
        FlowMatchingModel model = FlowMatchingModel.ReadJson(reader.ReadString()!);
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._skeleton = skeleton;
            target._model = model;
            target.MarkChanged();
        }
    }

    public static async ValueTask<FlowMatchingBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new FlowMatchingBlock();
        try
        {
            await source.ReadIntoAsync(result, cancellationToken).ConfigureAwait(false);
            result.AttachSource(source);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        writer.WriteMetadata(BlockTypeId, this);
        await WriteChunksAsync(writer, cancellationToken).ConfigureAwait(false);
    }
}
