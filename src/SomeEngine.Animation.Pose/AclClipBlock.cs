using System.Collections.Immutable;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

[AssetBlock("SomeEngine.Animation.Pose.AclClipBlock"), BinarySerializable]
public sealed partial class AclClipBlock : AnimationClipBlock, IAssetBlockWriter,
    IBinarySerializable<AclClipBlock>, IBinaryObjectReader<AclClipBlock>
{
    private AssetId<SkeletonAsset> _skeleton;
    private AclClipData _data = null!;

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.AclClipBlock");
    public static ulong SchemaFingerprint => 0x41434C434C495001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public override AssetId<SkeletonAsset> Skeleton => _skeleton;
    public override float Duration => _data.Duration;
    public override short RootMotionBone => _data.RootMotionBone;
    public override ImmutableArray<ClipEvent> Events => _data.Events;
    public override ClipSampler Sampler => ClipSampler.Create<AclClipData, AclClipSampler>(_data);
    public AclClipData Data => _data;

    public AclClipBlock(AssetId<SkeletonAsset> skeleton, AclClipData data) => (_skeleton, _data) = (skeleton, data);
    private AclClipBlock() { }
    private AclClipBlock(AclClipBlock source, object? syncRoot = null) : base(source, syncRoot)
        => (_skeleton, _data) = (source._skeleton, source._data);

    public override AclClipBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override AclClipBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public override IEnumerable<Guid> References()
    {
        if (!_skeleton.IsEmpty) yield return _skeleton.Value;
    }

    public static void Write(ref BinaryDataWriter writer, AclClipBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteGuid(value._skeleton.Value);
            AclClipData data = value._data;
            writer.WriteSingle(data.Duration);
            writer.WriteInt16(data.RootMotionBone);
            writer.WriteInt32(data.Events.Length);
            foreach (ClipEvent clipEvent in data.Events)
            {
                writer.WriteSingle(clipEvent.Time);
                writer.WriteUInt16(clipEvent.Id);
            }
            writer.WriteInt32(data.Bytes.Length);
            writer.WriteBytes(data.Bytes);
        }
    }

    public static AclClipBlock Read(ref BinaryDataReader reader)
    {
        var result = new AclClipBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, AclClipBlock target)
    {
        var skeleton = new AssetId<SkeletonAsset>(reader.ReadGuid());
        float duration = reader.ReadSingle();
        short rootMotionBone = reader.ReadInt16();
        int eventCount = reader.ReadCollectionCount("clip events", 8, 24);
        var events = ImmutableArray.CreateBuilder<ClipEvent>(eventCount);
        for (int index = 0; index < eventCount; index++) events.Add(new(reader.ReadSingle(), reader.ReadUInt16()));
        int byteCount = reader.ReadCollectionCount("ACL clip bytes", 1, 24);
        var data = new AclClipData(reader.ReadBytes(byteCount), duration, events.MoveToImmutable(), rootMotionBone);
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._skeleton = skeleton;
            target._data = data;
            target.MarkChanged();
        }
    }

    public static async ValueTask<AclClipBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new AclClipBlock();
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
