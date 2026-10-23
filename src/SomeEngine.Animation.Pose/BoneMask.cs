using SomeEngine.Assets;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Ready, immutable layer weights in a skeleton definition's own-bone order.</summary>
[AssetUse]
public sealed class BoneMask
{
    private readonly float[] _weights;
    public int BoneCount => _weights.Length;
    public ReadOnlySpan<float> Weights => _weights;

    public BoneMask(ReadOnlySpan<float> weights) : this(weights.ToArray()) { }
    internal BoneMask(float[] weights) => _weights = weights;
}

/// <summary>Persistent bone weights backed by one ready mask shared through ordinary references.</summary>
[AssetBlock("SomeEngine.Animation.Pose.BoneMaskBlock"), BinarySerializable]
public sealed partial class BoneMaskBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<BoneMaskBlock>, IBinaryObjectReader<BoneMaskBlock>
{
    private BoneMask _mask = new([]);

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.BoneMaskBlock");
    public static ulong SchemaFingerprint => 0x424D41534B763101UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public int BoneCount => _mask.BoneCount;
    public ReadOnlySpan<float> Weights => _mask.Weights;

    public BoneMaskBlock(ReadOnlySpan<float> weights) => _mask = new(weights);
    private BoneMaskBlock() { }
    private BoneMaskBlock(BoneMaskBlock source, object? syncRoot = null) : base(source, syncRoot) => _mask = source._mask;

    public override BoneMaskBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override BoneMaskBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    /// <summary>Returns the ready mask, kept valid by the caller's ordinary reference.</summary>
    public BoneMask CreateMask()
    {
        lock (SyncRoot) { RequireIdle(); return _mask; }
    }

    public static void Write(ref BinaryDataWriter writer, BoneMaskBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteInt32(value.BoneCount);
            foreach (float weight in value._mask.Weights) writer.WriteSingle(weight);
        }
    }

    public static BoneMaskBlock Read(ref BinaryDataReader reader)
    {
        var result = new BoneMaskBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, BoneMaskBlock target)
    {
        int count = reader.ReadCollectionCount("bone mask weights", sizeof(float), 24);
        var weights = new float[count];
        for (int bone = 0; bone < count; bone++) weights[bone] = reader.ReadSingle();
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._mask = new BoneMask(weights);
            target.MarkChanged();
        }
    }

    public static async ValueTask<BoneMaskBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new BoneMaskBlock();
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
