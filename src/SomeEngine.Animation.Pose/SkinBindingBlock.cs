using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Inverse bind matrices and bone indices in the source skin's joint order.</summary>
[AssetBlock("SomeEngine.Animation.Pose.SkinBindingBlock"), BinarySerializable]
public sealed partial class SkinBindingBlock : AssetBlock, IAssetBlockWriter,
    IBinarySerializable<SkinBindingBlock>, IBinaryObjectReader<SkinBindingBlock>
{
    private SkinBinding _binding = new([], []);

    public static Guid TypeId { get; } = BinaryTypeId.FromLogicalName("SomeEngine.Animation.Pose.SkinBindingBlock");
    public static ulong SchemaFingerprint => 0x534B494E76310001UL;
    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public int JointCount => _binding.JointCount;
    public ReadOnlySpan<Matrix4x4> InverseBindMatrices => _binding.InverseBindMatrices;
    public ReadOnlySpan<short> JointBones => _binding.JointBones;

    public SkinBindingBlock(ReadOnlySpan<Matrix4x4> inverseBindMatrices, ReadOnlySpan<short> jointBones)
    {
        _binding = new SkinBinding(inverseBindMatrices, jointBones);
    }

    private SkinBindingBlock() { }
    private SkinBindingBlock(SkinBindingBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        _binding = source._binding;
    }

    public override SkinBindingBlock Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override SkinBindingBlock Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }

    /// <summary>Returns the ready binding, kept valid by the caller's ordinary reference.</summary>
    public SkinBinding CreateSkinBinding()
    {
        lock (SyncRoot) { RequireIdle(); return _binding; }
    }

    public static void Write(ref BinaryDataWriter writer, SkinBindingBlock value)
    {
        lock (value.SyncRoot)
        {
            value.RequireIdle();
            writer.WriteInt32(value.JointCount);
            for (int joint = 0; joint < value.JointCount; joint++)
            {
                Matrix4x4 matrix = value._binding.InverseBindMatrices[joint];
                writer.WriteSingle(matrix.M11); writer.WriteSingle(matrix.M12); writer.WriteSingle(matrix.M13); writer.WriteSingle(matrix.M14);
                writer.WriteSingle(matrix.M21); writer.WriteSingle(matrix.M22); writer.WriteSingle(matrix.M23); writer.WriteSingle(matrix.M24);
                writer.WriteSingle(matrix.M31); writer.WriteSingle(matrix.M32); writer.WriteSingle(matrix.M33); writer.WriteSingle(matrix.M34);
                writer.WriteSingle(matrix.M41); writer.WriteSingle(matrix.M42); writer.WriteSingle(matrix.M43); writer.WriteSingle(matrix.M44);
                writer.WriteInt16(value._binding.JointBones[joint]);
            }
        }
    }

    public static SkinBindingBlock Read(ref BinaryDataReader reader)
    {
        var result = new SkinBindingBlock();
        try { ReadInto(ref reader, result); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void ReadInto(ref BinaryDataReader reader, SkinBindingBlock target)
    {
        int count = reader.ReadCollectionCount("skin joint bindings", 16 * sizeof(float) + sizeof(short), 48);
        var matrices = new Matrix4x4[count];
        var bones = new short[count];
        for (int joint = 0; joint < count; joint++)
        {
            matrices[joint] = new(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            bones[joint] = reader.ReadInt16();
        }
        lock (target.SyncRoot)
        {
            target.RequireIdle();
            target._binding = new SkinBinding(matrices, bones);
            target.MarkChanged();
        }
    }

    public static async ValueTask<SkinBindingBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        var result = new SkinBindingBlock();
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
