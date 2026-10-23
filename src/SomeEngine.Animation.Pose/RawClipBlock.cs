using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

[AssetBlock("SomeEngine.Animation.Pose.RawClipBlock"), BinarySerializable]
public sealed partial class RawClipBlock : AnimationClipBlock, IAssetBlockWriter,
    IBinarySerializable<RawClipBlock>, IBinaryObjectReader<RawClipBlock>
{
    private AssetId<SkeletonAsset> _skeleton;
    private RawClipData _data = new(0, []);

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.RawClipBlock");
    public static ulong SchemaFingerprint => 0x524157434C495001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public override AssetId<SkeletonAsset> Skeleton => _skeleton;
    public override float Duration => _data.Duration;
    public override short RootMotionBone => _data.RootMotionBone;
    public override ImmutableArray<ClipEvent> Events => _data.Events;
    public override ClipSampler Sampler => ClipSampler.Create<RawClipData, RawClipSampler>(_data);
    public RawClipData Data => _data;

    public RawClipBlock(AssetId<SkeletonAsset> skeleton, RawClipData data) => (_skeleton, _data) = (skeleton, data);
    private RawClipBlock() { }
    private RawClipBlock(RawClipBlock source, object? syncRoot = null) : base(source, syncRoot)
        => (_skeleton, _data) = (source._skeleton, source._data);

    public override RawClipBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override RawClipBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public override IEnumerable<Guid> References()
    {
        if (!_skeleton.IsEmpty) yield return _skeleton.Value;
    }

    public static void Write(ref BinaryDataWriter writer, RawClipBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteGuid(value._skeleton.Value);
            RawClipData data = value._data;
            writer.WriteSingle(data.Duration);
            writer.WriteInt16(data.RootMotionBone);
            writer.WriteInt32(data.Events.Length);
            foreach (ClipEvent clipEvent in data.Events)
            {
                writer.WriteSingle(clipEvent.Time);
                writer.WriteUInt16(clipEvent.Id);
            }
            writer.WriteInt32(data.Channels.Length);
            foreach (RawClipChannel channel in data.Channels)
            {
                writer.WriteInt16(channel.Bone);
                writer.WriteByte((byte)channel.Property);
                writer.WriteByte((byte)channel.Interpolation);
                writer.WriteInt32(channel.Times.Length);
                foreach (float time in channel.Times) writer.WriteSingle(time);
                writer.WriteInt32(channel.Values.Length);
                foreach (Vector4 sample in channel.Values)
                {
                    writer.WriteSingle(sample.X); writer.WriteSingle(sample.Y);
                    writer.WriteSingle(sample.Z); writer.WriteSingle(sample.W);
                }
            }
        }
    }

    public static RawClipBlock Read(ref BinaryDataReader reader)
    {
        var result = new RawClipBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, RawClipBlock target)
    {
        var skeleton = new AssetId<SkeletonAsset>(reader.ReadGuid());
        float duration = reader.ReadSingle();
        short rootMotionBone = reader.ReadInt16();
        int eventCount = reader.ReadCollectionCount("clip events", 8, 24);
        var events = ImmutableArray.CreateBuilder<ClipEvent>(eventCount);
        for (int index = 0; index < eventCount; index++) events.Add(new(reader.ReadSingle(), reader.ReadUInt16()));
        int channelCount = reader.ReadCollectionCount("raw clip channels", 64, 24);
        var channels = ImmutableArray.CreateBuilder<RawClipChannel>(channelCount);
        for (int index = 0; index < channelCount; index++)
        {
            short bone = reader.ReadInt16();
            var property = (BoneTransformProperty)reader.ReadByte();
            var interpolation = (ClipInterpolation)reader.ReadByte();
            int timeCount = reader.ReadCollectionCount("raw clip times", sizeof(float), 24);
            var times = new float[timeCount];
            for (int key = 0; key < times.Length; key++) times[key] = reader.ReadSingle();
            int valueCount = reader.ReadCollectionCount("raw clip values", 4 * sizeof(float), 24);
            var values = new Vector4[valueCount];
            for (int key = 0; key < values.Length; key++)
                values[key] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            channels.Add(new(bone, property, interpolation, times, values));
        }
        var data = new RawClipData(duration, channels.MoveToImmutable(), events.MoveToImmutable(), rootMotionBone);
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._skeleton = skeleton;
            target._data = data;
            target.MarkChanged();
        }
    }

    public static async ValueTask<RawClipBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new RawClipBlock();
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
