using SomeEngine.Assets;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Parent-ordered bones, their local reference pose and source joint correspondence.</summary>
[AssetBlock("SomeEngine.Animation.Pose.SkeletonBlock"), BinarySerializable]
public sealed partial class SkeletonBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<SkeletonBlock>, IBinaryObjectReader<SkeletonBlock>
{
    private Skeleton _skeleton = new([], [], [], []);

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.SkeletonBlock");
    public static ulong SchemaFingerprint => 0x534B454C76310001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public int BoneCount => _skeleton.BoneCount;
    public ReadOnlySpan<short> Parents => _skeleton.Parents;
    public ReadOnlySpan<BoneTransform> ReferencePose => _skeleton.ReferencePose;
    public ReadOnlySpan<uint> NameHashes => _skeleton.NameHashes;
    /// <summary>Bone index for each source skin joint, in the original joint order.</summary>
    public ReadOnlySpan<short> JointBones => _skeleton.JointBones;

    public SkeletonBlock(ReadOnlySpan<short> parents, ReadOnlySpan<BoneTransform> referencePose,
        ReadOnlySpan<uint> nameHashes, ReadOnlySpan<short> jointBones = default)
    {
        _skeleton = new Skeleton(parents, referencePose, nameHashes, jointBones);
    }

    private SkeletonBlock() { }
    private SkeletonBlock(SkeletonBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _skeleton = source._skeleton;
    }

    public override SkeletonBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override SkeletonBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    /// <summary>Returns the ready bone data, kept valid by the caller's ordinary reference.</summary>
    public Skeleton CreateSkeleton()
    {
        lock (SyncRoot) { RequireIdle(); return _skeleton; }
    }

    public static void Write(ref BinaryDataWriter writer, SkeletonBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            Skeleton skeleton = value._skeleton;
            writer.WriteInt32(skeleton.BoneCount);
            for (int bone = 0; bone < skeleton.BoneCount; bone++)
            {
                writer.WriteInt16(skeleton.Parents[bone]);
                BoneTransform transform = skeleton.ReferencePose[bone];
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
                writer.WriteUInt32(skeleton.NameHashes[bone]);
            }
            writer.WriteInt32(skeleton.JointBones.Length);
            foreach (short bone in skeleton.JointBones) writer.WriteInt16(bone);
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
        var skeleton = new Skeleton(parents, referencePose, nameHashes, jointBones);
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._skeleton = skeleton;
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
