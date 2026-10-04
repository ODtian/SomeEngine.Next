using SomeEngine.Assets;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Parent-ordered bones, their local reference pose and source joint correspondence.</summary>
[AssetBlock("SomeEngine.Animation.Pose.SkeletonBlock"), BinarySerializable]
public sealed partial class SkeletonBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<SkeletonBlock>, IBinaryObjectReader<SkeletonBlock>
{
    private short[] _parents = [];
    private BoneTransform[] _referencePose = [];
    private uint[] _nameHashes = [];
    private short[] _jointBones = [];

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.SkeletonBlock");
    public static ulong SchemaFingerprint => 0x534B454C76310001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public int BoneCount => _parents.Length;
    public ReadOnlySpan<short> Parents => _parents;
    public ReadOnlySpan<BoneTransform> ReferencePose => _referencePose;
    public ReadOnlySpan<uint> NameHashes => _nameHashes;
    /// <summary>Bone index for each source skin joint, in the original joint order.</summary>
    public ReadOnlySpan<short> JointBones => _jointBones;

    public SkeletonBlock(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones = default)
    {
        CheckContent(parents, referencePose, nameHashes, jointBones);
        _parents = parents.ToArray();
        _referencePose = referencePose.ToArray();
        _nameHashes = nameHashes.ToArray();
        _jointBones = jointBones.ToArray();
    }

    private SkeletonBlock() { }
    private SkeletonBlock(SkeletonBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _parents = source._parents;
        _referencePose = source._referencePose;
        _nameHashes = source._nameHashes;
        _jointBones = source._jointBones;
    }

    public override SkeletonBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override SkeletonBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void CheckContent(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones)
    {
        DevelopmentCheck.Require(parents.Length <= short.MaxValue + 1, "Bone indices must fit in a signed 16-bit value.");
        DevelopmentCheck.Require(referencePose.Length == parents.Length && nameHashes.Length == parents.Length,
            "Each bone requires a parent index, reference transform and name hash.");
        for (int bone = 0; bone < parents.Length; bone++)
            DevelopmentCheck.Require(parents[bone] >= -1 && parents[bone] < bone, "A bone's parent must precede it, or be -1 for a root.");
        foreach (short bone in jointBones)
            DevelopmentCheck.Require(bone >= 0 && bone < parents.Length, "Each source joint must reference a bone.");
    }

    public static void Write(ref BinaryDataWriter writer, SkeletonBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteInt32(value.BoneCount);
            for (int bone = 0; bone < value.BoneCount; bone++)
            {
                writer.WriteInt16(value._parents[bone]);
                BoneTransform transform = value._referencePose[bone];
                writer.WriteSingle(transform.Translation.X);
                writer.WriteSingle(transform.Translation.Y);
                writer.WriteSingle(transform.Translation.Z);
                writer.WriteSingle(transform.Rotation.X);
                writer.WriteSingle(transform.Rotation.Y);
                writer.WriteSingle(transform.Rotation.Z);
                writer.WriteSingle(transform.Rotation.W);
                writer.WriteSingle(transform.Scale.X);
                writer.WriteSingle(transform.Scale.Y);
                writer.WriteSingle(transform.Scale.Z);
                writer.WriteUInt32(value._nameHashes[bone]);
            }
            writer.WriteInt32(value._jointBones.Length);
            foreach (short bone in value._jointBones) writer.WriteInt16(bone);
        }
    }

    public static SkeletonBlock Read(ref BinaryDataReader reader)
    {
        var result = new SkeletonBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, SkeletonBlock target)
    {
        int count = reader.ReadCollectionCount("skeleton bones", 46, 72);
        if (count > short.MaxValue + 1) throw new InvalidDataException("The skeleton exceeds the 16-bit bone index range.");
        var parents = new short[count];
        var referencePose = new BoneTransform[count];
        var nameHashes = new uint[count];
        for (int bone = 0; bone < count; bone++)
        {
            parents[bone] = reader.ReadInt16();
            referencePose[bone] = new()
            {
                Translation = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Rotation = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                Scale = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            };
            nameHashes[bone] = reader.ReadUInt32();
        }
        int jointCount = reader.ReadCollectionCount("skeleton joint correspondence", sizeof(short), 24);
        var jointBones = new short[jointCount];
        for (int joint = 0; joint < jointCount; joint++) jointBones[joint] = reader.ReadInt16();
        CheckContent(parents, referencePose, nameHashes, jointBones);
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._parents = parents;
            target._referencePose = referencePose;
            target._nameHashes = nameHashes;
            target._jointBones = jointBones;
            target.MarkChanged();
        }
    }

    public static async ValueTask<SkeletonBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new SkeletonBlock();
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
