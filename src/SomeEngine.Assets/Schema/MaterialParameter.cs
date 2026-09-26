using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

public readonly record struct MaterialParameter(string Name, MaterialParameterKind Kind)
{
    public AssetId<TextureAsset> Texture { get; init; }
    public SamplerDesc Sampler { get; init; }
    public float Float { get; init; }
    public int Int { get; init; }
    public uint UInt { get; init; }
    public bool Bool { get; init; }
    public Vector2 Vector2 { get; init; }
    public Vector3 Vector3 { get; init; }
    public Vector4 Vector4 { get; init; }
    public Matrix4x4 Matrix4x4 { get; init; }

    internal MaterialValue ToValue() => new(Name, Kind)
    {
        Sampler = Sampler, Float = Float, Int = Int, UInt = UInt, Bool = Bool,
        Vector2 = Vector2, Vector3 = Vector3, Vector4 = Vector4, Matrix4x4 = Matrix4x4,
    };

    internal static void Encode(ref BinaryDataWriter writer, MaterialParameter value)
    {
        writer.WriteString(value.Name);
        writer.WriteByte((byte)value.Kind);
        WriteParameter(ref writer, value);
    }
    internal static MaterialParameter Decode(ref BinaryDataReader reader)
    {
        string name = reader.ReadString() ?? throw new InvalidDataException("Material parameter has no name.");
        return ReadParameter(ref reader, name, (MaterialParameterKind)reader.ReadByte());
    }
    private static void WriteParameter(ref BinaryDataWriter writer, MaterialParameter value)
    {
        switch (value.Kind)
        {
            case MaterialParameterKind.Texture: writer.WriteGuid(value.Texture.Value); break;
            case MaterialParameterKind.Sampler: WriteSampler(ref writer, value.Sampler); break;
            case MaterialParameterKind.Float: writer.WriteSingle(value.Float); break;
            case MaterialParameterKind.Int: writer.WriteInt32(value.Int); break;
            case MaterialParameterKind.UInt: writer.WriteUInt32(value.UInt); break;
            case MaterialParameterKind.Bool: writer.WriteBoolean(value.Bool); break;
            case MaterialParameterKind.Vector2: Write(ref writer, value.Vector2); break;
            case MaterialParameterKind.Vector3: Write(ref writer, value.Vector3); break;
            case MaterialParameterKind.Vector4: Write(ref writer, value.Vector4); break;
            case MaterialParameterKind.Matrix4x4: Write(ref writer, value.Matrix4x4); break;
            default: throw new InvalidDataException("Unknown material parameter kind.");
        }
    }

    private static MaterialParameter ReadParameter(ref BinaryDataReader reader, string name, MaterialParameterKind kind)
    {
        return kind switch
        {
            MaterialParameterKind.Texture => new(name, kind) { Texture = new(reader.ReadGuid()) },
            MaterialParameterKind.Sampler => new(name, kind) { Sampler = ReadSampler(ref reader) },
            MaterialParameterKind.Float => new(name, kind) { Float = reader.ReadSingle() },
            MaterialParameterKind.Int => new(name, kind) { Int = reader.ReadInt32() },
            MaterialParameterKind.UInt => new(name, kind) { UInt = reader.ReadUInt32() },
            MaterialParameterKind.Bool => new(name, kind) { Bool = reader.ReadBoolean() },
            MaterialParameterKind.Vector2 => new(name, kind) { Vector2 = new(reader.ReadSingle(), reader.ReadSingle()) },
            MaterialParameterKind.Vector3 => new(name, kind) { Vector3 = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) },
            MaterialParameterKind.Vector4 => new(name, kind) { Vector4 = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) },
            MaterialParameterKind.Matrix4x4 => new(name, kind) { Matrix4x4 = ReadMatrix(ref reader) },
            _ => throw new InvalidDataException("Unknown material parameter kind."),
        };
    }

    private static void WriteSampler(ref BinaryDataWriter writer, in SamplerDesc value)
    {
        writer.WriteInt32((int)value.MinFilter);
        writer.WriteInt32((int)value.MagFilter);
        writer.WriteInt32((int)value.MipFilter);
        writer.WriteInt32((int)value.AddressU);
        writer.WriteInt32((int)value.AddressV);
        writer.WriteInt32((int)value.AddressW);
        writer.WriteSingle(value.MipLodBias);
        writer.WriteUInt32(value.MaximumAnisotropy);
        writer.WriteBoolean(value.Comparison.HasValue);
        if (value.Comparison.HasValue)
            writer.WriteInt32((int)value.Comparison.Value);
        Write(ref writer, value.BorderColor);
        writer.WriteSingle(value.MinimumLod);
        writer.WriteSingle(value.MaximumLod);
        writer.WriteString(value.Label);
    }

    private static SamplerDesc ReadSampler(ref BinaryDataReader reader)
    {
        var min = (FilterType)reader.ReadInt32();
        var mag = (FilterType)reader.ReadInt32();
        var mip = (FilterType)reader.ReadInt32();
        var u = (AddressType)reader.ReadInt32();
        var v = (AddressType)reader.ReadInt32();
        var w = (AddressType)reader.ReadInt32();
        float bias = reader.ReadSingle();
        uint anisotropy = reader.ReadUInt32();
        CompareOperation? comparison = reader.ReadBoolean()
            ? (CompareOperation)reader.ReadInt32()
            : null;
        Vector4 border = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float minimumLod = reader.ReadSingle();
        float maximumLod = reader.ReadSingle();
        string? label = reader.ReadString();
        if (!Enum.IsDefined(min) || !Enum.IsDefined(mag) || !Enum.IsDefined(mip) ||
            !Enum.IsDefined(u) || !Enum.IsDefined(v) || !Enum.IsDefined(w) ||
            (comparison.HasValue && !Enum.IsDefined(comparison.Value)) || anisotropy == 0)
            throw new InvalidDataException("Material sampler contains an invalid enum or anisotropy value.");
        return new SamplerDesc(
            min, mag, mip, u, v, w, bias, anisotropy, comparison, border, minimumLod, maximumLod, label);
    }

    private static void Write(ref BinaryDataWriter writer, Vector2 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    private static void Write(ref BinaryDataWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
    }

    private static void Write(ref BinaryDataWriter writer, Vector4 value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
        writer.WriteSingle(value.Z);
        writer.WriteSingle(value.W);
    }

    private static void Write(ref BinaryDataWriter writer, Matrix4x4 value)
    {
        writer.WriteSingle(value.M11); writer.WriteSingle(value.M12); writer.WriteSingle(value.M13); writer.WriteSingle(value.M14);
        writer.WriteSingle(value.M21); writer.WriteSingle(value.M22); writer.WriteSingle(value.M23); writer.WriteSingle(value.M24);
        writer.WriteSingle(value.M31); writer.WriteSingle(value.M32); writer.WriteSingle(value.M33); writer.WriteSingle(value.M34);
        writer.WriteSingle(value.M41); writer.WriteSingle(value.M42); writer.WriteSingle(value.M43); writer.WriteSingle(value.M44);
    }

    private static Matrix4x4 ReadMatrix(ref BinaryDataReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

}
