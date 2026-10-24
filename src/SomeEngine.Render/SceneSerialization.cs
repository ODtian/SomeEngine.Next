using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Render.Components;
using SomeEngine.Serialization;

namespace SomeEngine.Render;

/// <summary>Codecs for the components used by the sample scenes, using the normal ECS World format.</summary>
public static class SceneSerialization
{
    public static SerializationRegistry CreateRegistry() => new SerializationRegistry()
        .Register<WorldTransform, TransformCodec>(Key(nameof(WorldTransform), "rotation:4f;position:3f;stretch:3f;scale:f"))
        .RegisterHierarchyDomain<DefaultHierarchyDomain>()
        .Register<MeshSource, MeshSourceCodec>(Key(nameof(MeshSource), "mesh:external;bounds:f"))
        .Register<SkeletonSource, SkeletonSourceCodec>(Key(nameof(SkeletonSource), "skeleton:external"))
        .Register<MeshMotion, MotionCodec>(Key(nameof(MeshMotion), "amplitude:3f;seed:u32"))
        .Register<SceneCamera, CameraCodec>(Key(nameof(SceneCamera), "position:3f;target:3f;up:3f;fov:f;near:f;far:f"))
        .Register<DirectionalLight, DirectionalCodec>(Key(nameof(DirectionalLight), "direction:3f;color:3f;intensity:f;layers:u32"))
        .Register<PointLight, PointCodec>(Key(nameof(PointLight), "position:3f;range:f;color:3f;intensity:f;layers:u32"))
        .Register<SpotLight, SpotCodec>(Key(nameof(SpotLight), "position:3f;range:f;direction:3f;inner:f;outer:f;color:3f;intensity:f;layers:u32"));

    private static SerializationTypeKey Key(string name, string fields)
        => new(BinaryTypeId.FromLogicalName("SomeEngine.Scene." + name), "SomeEngine.Scene." + name, BinaryFieldKey.FromName(fields));
    private static void WriteVector(ref DataWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z);
    }
    private static Vector3 ReadVector(ref DataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private struct TransformCodec : ICanonicalComponentCodec<WorldTransform>
    {
        public void Write(ref DataWriter writer, in WorldTransform value)
        {
            Quaternion rotation = value.Qvvs.Rotation;
            writer.WriteSingle(rotation.X); writer.WriteSingle(rotation.Y); writer.WriteSingle(rotation.Z); writer.WriteSingle(rotation.W);
            WriteVector(ref writer, value.Qvvs.Position); WriteVector(ref writer, value.Qvvs.Stretch); writer.WriteSingle(value.Qvvs.Scale);
        }
        public void Read(ref DataReader reader, out WorldTransform value)
        {
            Quaternion rotation = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            value = new() { Qvvs = new TransformQvvs(ReadVector(ref reader), rotation) { Stretch = ReadVector(ref reader), Scale = reader.ReadSingle() } };
        }
    }
    private struct MeshSourceCodec : ICanonicalComponentCodec<MeshSource>
    {
        public void Write(ref DataWriter writer, in MeshSource value)
        {
            writer.WriteExternalReference(new(value.Mesh.Value)); writer.WriteSingle(value.BoundsExpansion);
        }
        public void Read(ref DataReader reader, out MeshSource value)
            => value = new() { Mesh = new(reader.ReadExternalReference().Value), BoundsExpansion = reader.ReadSingle() };
    }
    private struct SkeletonSourceCodec : ICanonicalComponentCodec<SkeletonSource>
    {
        public void Write(ref DataWriter writer, in SkeletonSource value)
            => writer.WriteExternalReference(new(value.Skeleton.Value));
        public void Read(ref DataReader reader, out SkeletonSource value)
            => value = new() { Skeleton = new(reader.ReadExternalReference().Value) };
    }
    private struct XrayCodec : ICanonicalComponentCodec<Xray>
    {
        public void Write(ref DataWriter writer, in Xray value)
        {
            WriteVector(ref writer, value.Visible.ColorMultiplier); writer.WriteSingle(value.Visible.Opacity);
            WriteVector(ref writer, value.Occluded.ColorMultiplier); writer.WriteSingle(value.Occluded.Opacity);
            writer.WriteSingle(value.MinimumOcclusion); writer.WriteSingle(value.MaximumOcclusion);
        }

        public void Read(ref DataReader reader, out Xray value) => value = new(
            new XrayBlend(ReadVector(ref reader), reader.ReadSingle()),
            new XrayBlend(ReadVector(ref reader), reader.ReadSingle()), reader.ReadSingle(), reader.ReadSingle());
    }

    private struct MotionCodec : ICanonicalComponentCodec<MeshMotion>
    {
        public void Write(ref DataWriter writer, in MeshMotion value) { WriteVector(ref writer, value.Amplitude); writer.WriteUInt32(value.Seed); }
        public void Read(ref DataReader reader, out MeshMotion value) => value = new() { Amplitude = ReadVector(ref reader), Seed = reader.ReadUInt32() };
    }
    private struct CameraCodec : ICanonicalComponentCodec<SceneCamera>
    {
        public void Write(ref DataWriter writer, in SceneCamera value)
        {
            WriteVector(ref writer, value.Position); WriteVector(ref writer, value.Target); WriteVector(ref writer, value.Up);
            writer.WriteSingle(value.VerticalFieldOfView); writer.WriteSingle(value.NearPlane); writer.WriteSingle(value.FarPlane);
        }
        public void Read(ref DataReader reader, out SceneCamera value) => value = new()
        {
            Position = ReadVector(ref reader), Target = ReadVector(ref reader), Up = ReadVector(ref reader),
            VerticalFieldOfView = reader.ReadSingle(), NearPlane = reader.ReadSingle(), FarPlane = reader.ReadSingle(),
        };
    }
    private struct DirectionalCodec : ICanonicalComponentCodec<DirectionalLight>
    {
        public void Write(ref DataWriter writer, in DirectionalLight value)
        {
            WriteVector(ref writer, value.Direction);
            WriteVector(ref writer, value.Color);
            writer.WriteSingle(value.Intensity);
            writer.WriteUInt32(value.LayerMask);
        }
        public void Read(ref DataReader reader, out DirectionalLight value) => value = new(
            ReadVector(ref reader), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
    private struct PointCodec : ICanonicalComponentCodec<PointLight>
    {
        public void Write(ref DataWriter writer, in PointLight value)
        {
            WriteVector(ref writer, value.Position);
            writer.WriteSingle(value.Range);
            WriteVector(ref writer, value.Color);
            writer.WriteSingle(value.Intensity);
            writer.WriteUInt32(value.LayerMask);
        }
        public void Read(ref DataReader reader, out PointLight value) => value = new(
            ReadVector(ref reader), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
    private struct SpotCodec : ICanonicalComponentCodec<SpotLight>
    {
        public void Write(ref DataWriter writer, in SpotLight value)
        {
            WriteVector(ref writer, value.Position);
            writer.WriteSingle(value.Range);
            WriteVector(ref writer, value.Direction);
            writer.WriteSingle(value.InnerConeCos);
            writer.WriteSingle(value.OuterConeCos);
            WriteVector(ref writer, value.Color);
            writer.WriteSingle(value.Intensity);
            writer.WriteUInt32(value.LayerMask);
        }
        public void Read(ref DataReader reader, out SpotLight value) => value = new(
            ReadVector(ref reader), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
}
