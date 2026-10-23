using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

[AssetBlock("SomeEngine.Animation.Pose.QuantizedClipBlock"), BinarySerializable]
public sealed partial class QuantizedClipBlock : AnimationClipBlock, IAssetBlockWriter,
    IBinarySerializable<QuantizedClipBlock>, IBinaryObjectReader<QuantizedClipBlock>
{
    private AssetId<SkeletonAsset> _skeleton;
    private QuantizedClipData _data = null!;
    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.QuantizedClipBlock");
    public static ulong SchemaFingerprint => 0x5155414E54434C01UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public override AssetId<SkeletonAsset> Skeleton => _skeleton;
    public override float Duration => _data.Duration;
    public override short RootMotionBone => _data.RootMotionBone;
    public override ImmutableArray<ClipEvent> Events => _data.Events;
    public override ClipSampler Sampler => ClipSampler.Create<QuantizedClipData, QuantizedClipSampler>(_data);
    public QuantizedClipData Data => _data;

    public QuantizedClipBlock(AssetId<SkeletonAsset> skeleton, QuantizedClipData data) => (_skeleton, _data) = (skeleton, data);
    private QuantizedClipBlock() { }
    private QuantizedClipBlock(QuantizedClipBlock source, object? syncRoot = null) : base(source, syncRoot)
        => (_skeleton, _data) = (source._skeleton, source._data);
    public override QuantizedClipBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override QuantizedClipBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public override IEnumerable<Guid> References() { if (!_skeleton.IsEmpty) yield return _skeleton.Value; }

    public static void Write(ref BinaryDataWriter writer, QuantizedClipBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteGuid(value._skeleton.Value);
            QuantizedClipData data = value._data;
            writer.WriteSingle(data.Duration);
            writer.WriteInt16(data.RootMotionBone);
            writer.WriteInt32(data.Events.Length);
            foreach (ClipEvent marker in data.Events) { writer.WriteSingle(marker.Time); writer.WriteUInt16(marker.Id); }
            writer.WriteInt32(data.SampleCount);
            writer.WriteInt32(data.BoneCount);
            foreach (QuantizedBoneRange range in data.Ranges)
            {
                WriteVector(ref writer, range.TranslationMinimum); WriteVector(ref writer, range.TranslationExtent);
                WriteVector(ref writer, range.ScaleMinimum); WriteVector(ref writer, range.ScaleExtent);
            }
            writer.WriteInt32(data.Samples.Length);
            foreach (uint sample in data.Samples) writer.WriteUInt32(sample);
        }
    }

    private static void WriteVector(ref BinaryDataWriter writer, Vector4 value)
    { writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z); writer.WriteSingle(value.W); }
    private static Vector4 ReadVector(ref BinaryDataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    public static QuantizedClipBlock Read(ref BinaryDataReader reader)
    {
        var result = new QuantizedClipBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, QuantizedClipBlock target)
    {
        var skeleton = new AssetId<SkeletonAsset>(reader.ReadGuid());
        float duration = reader.ReadSingle();
        short root = reader.ReadInt16();
        int eventCount = reader.ReadCollectionCount("clip events", 8, 24);
        var events = ImmutableArray.CreateBuilder<ClipEvent>(eventCount);
        for (int index = 0; index < eventCount; index++) events.Add(new(reader.ReadSingle(), reader.ReadUInt16()));
        int samples = reader.ReadInt32();
        int bones = reader.ReadCollectionCount("quantized bone ranges", 64, 24);
        var ranges = new QuantizedBoneRange[bones];
        for (int bone = 0; bone < bones; bone++) ranges[bone] = new(ReadVector(ref reader), ReadVector(ref reader), ReadVector(ref reader), ReadVector(ref reader));
        int wordCount = reader.ReadCollectionCount("quantized TRS samples", sizeof(uint), 24);
        var words = new uint[wordCount];
        for (int index = 0; index < words.Length; index++) words[index] = reader.ReadUInt32();
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._skeleton = skeleton;
            target._data = new(duration, samples, ranges, words, events.MoveToImmutable(), root);
            target.MarkChanged();
        }
    }

    public static async ValueTask<QuantizedClipBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new QuantizedClipBlock();
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
