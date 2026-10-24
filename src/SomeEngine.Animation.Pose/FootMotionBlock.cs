using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>The clip asset's persistent foot data, backed by one ready motion object.</summary>
[AssetBlock("SomeEngine.Animation.Pose.FootMotionBlock"), BinarySerializable]
public sealed partial class FootMotionBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<FootMotionBlock>, IBinaryObjectReader<FootMotionBlock>
{
    private AssetId<SkeletonAsset> _skeleton;
    private FootMotion _motion = null!;
    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.FootMotionBlock");
    public static ulong SchemaFingerprint => 0x464F4F544D4F5401UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;
    public AssetId<SkeletonAsset> Skeleton => _skeleton;

    public FootMotionBlock(AssetId<SkeletonAsset> skeleton, FootMotion motion) => (_skeleton, _motion) = (skeleton, motion);
    private FootMotionBlock() { }
    private FootMotionBlock(FootMotionBlock source, object? syncRoot = null) : base(source, syncRoot)
        => (_skeleton, _motion) = (source._skeleton, source._motion);
    public override FootMotionBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override FootMotionBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public override IEnumerable<Guid> References()
    {
        if (!_skeleton.IsEmpty) yield return _skeleton.Value;
    }

    public FootMotion CreateMotion() { lock (SyncRoot) { RequireIdle(); return _motion; } }

    public static void Write(ref BinaryDataWriter writer, FootMotionBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            FootMotion motion = value._motion;
            writer.WriteGuid(value._skeleton.Value);
            writer.WriteInt16(motion.RootBone);
            FootAnalysisSettings settings = motion.Settings;
            writer.WriteSingle(settings.SampleRate); writer.WriteSingle(settings.GroundHeight);
            writer.WriteSingle(settings.HeightTolerance); writer.WriteSingle(settings.SpeedTolerance); writer.WriteInt32(settings.UpAxis);
            writer.WriteInt32(motion.Root.Keys.Length);
            foreach (RootMotionKey key in motion.Root.Keys)
            {
                writer.WriteSingle(key.Time); WriteVector(ref writer, key.Translation);
                writer.WriteSingle(key.Rotation.X); writer.WriteSingle(key.Rotation.Y);
                writer.WriteSingle(key.Rotation.Z); writer.WriteSingle(key.Rotation.W);
            }
            writer.WriteInt32(motion.FootCount);
            foreach (FootTrack foot in motion.Feet)
            {
                writer.WriteInt16(foot.Bones.Heel); writer.WriteInt16(foot.Bones.Toe);
                for (int sample = 0; sample < foot.Heel.Length; sample++)
                {
                    WriteVector(ref writer, foot.Heel[sample]); WriteVector(ref writer, foot.Toe[sample]);
                }
                writer.WriteInt32(foot.Contacts.Length);
                foreach (FootContactSegment contact in foot.Contacts)
                {
                    writer.WriteSingle(contact.Start); writer.WriteSingle(contact.End); writer.WriteByte((byte)contact.Kind);
                }
            }
            writer.WriteInt32(motion.Markers.Length);
            foreach (FootContactMarker marker in motion.Markers)
            {
                writer.WriteInt32(marker.Foot); writer.WriteSingle(marker.Start); writer.WriteSingle(marker.End);
                writer.WriteByte((byte)marker.Kind);
            }
        }
    }

    public static FootMotionBlock Read(ref BinaryDataReader reader)
    {
        var result = new FootMotionBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, FootMotionBlock target)
    {
        var skeleton = new AssetId<SkeletonAsset>(reader.ReadGuid());
        short rootBone = reader.ReadInt16();
        var settings = new FootAnalysisSettings(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadInt32());
        var root = new RootMotionKey[reader.ReadCollectionCount("foot root keys", 32, 24)];
        for (int sample = 0; sample < root.Length; sample++)
        {
            float time = reader.ReadSingle();
            Vector3 translation = ReadVector(ref reader);
            var rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            root[sample] = new(time, translation, rotation);
        }
        var feet = new FootTrack[reader.ReadCollectionCount("foot tracks", 8, 24)];
        for (int foot = 0; foot < feet.Length; foot++)
        {
            var bones = new FootBones(reader.ReadInt16(), reader.ReadInt16());
            var heel = new Vector3[root.Length];
            var toe = new Vector3[root.Length];
            for (int sample = 0; sample < root.Length; sample++)
            {
                heel[sample] = ReadVector(ref reader); toe[sample] = ReadVector(ref reader);
            }
            var contacts = new FootContactSegment[reader.ReadCollectionCount("foot contacts", 9, 24)];
            for (int index = 0; index < contacts.Length; index++)
                contacts[index] = new(reader.ReadSingle(), reader.ReadSingle(), (FootContactKind)reader.ReadByte());
            feet[foot] = new(bones, heel, toe, contacts);
        }
        var markers = new FootContactMarker[reader.ReadCollectionCount("foot markers", 13, 24)];
        for (int index = 0; index < markers.Length; index++)
            markers[index] = new(reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle(), (FootContactKind)reader.ReadByte());
        var motion = new FootMotion(rootBone, new RootMotionPath(root), feet, settings, markers);
        lock (target.SyncRoot)
        {
            target.RequireIdle(); target._skeleton = skeleton; target._motion = motion; target.MarkChanged();
        }
    }

    private static void WriteVector(ref BinaryDataWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z);
    }

    private static Vector3 ReadVector(ref BinaryDataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    public static async ValueTask<FootMotionBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new FootMotionBlock();
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
