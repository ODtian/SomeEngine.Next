using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Render.Components;
using SomeEngine.Serialization;

namespace SomeEngine.Render;

/// <summary>The registry for the components saved in scenes, using the normal ECS World format.</summary>
public static class SceneSerialization
{
    public static SaveRegistry CreateRegistry() => new SaveRegistry()
        .Register<WorldTransform, TransformSerializer>(Key(nameof(WorldTransform), "rotation:4f;position:3f;stretch:3f;scale:f"))
        .RegisterHierarchy<DefaultHierarchyDomain>(Key(nameof(DefaultHierarchyDomain), "parent:entity;children:entities;order:b"))
        .Register<MeshSource, MeshSourceSerializer>(Key(nameof(MeshSource), "mesh:external;bounds:f;depth:u32;kind:u32;player:u32;visibility:u32"))
        .Register<SkeletonSource, SkeletonSourceSerializer>(Key(nameof(SkeletonSource), "skeleton:external"))
        .Register<MeshMotion, MotionSerializer>(Key(nameof(MeshMotion), "amplitude:3f;seed:u32"))
        .Register<SceneCamera, CameraSerializer>(Key(nameof(SceneCamera), "position:3f;target:3f;up:3f;fov:f;near:f;far:f"))
        .Register<DirectionalLight, DirectionalSerializer>(Key(nameof(DirectionalLight), "direction:3f;color:3f;intensity:f;layers:u32"))
        .Register<PointLight, PointSerializer>(Key(nameof(PointLight), "position:3f;range:f;color:3f;intensity:f;layers:u32"))
        .Register<SpotLight, SpotSerializer>(Key(nameof(SpotLight), "position:3f;range:f;direction:3f;inner:f;outer:f;color:3f;intensity:f;layers:u32"))
        .Register<WorldOrigin, OriginSerializer>(Key(nameof(WorldOrigin), "x:f64;y:f64;z:f64"))
        .Register<Xray, XraySerializer>(Key(nameof(Xray), "visible:4f;occluded:4f;minimum:f;maximum:f"));

    private static SavedTypeKey Key(string name, string fields)
        => new(BinaryTypeId.FromLogicalName("SomeEngine.Scene." + name), "SomeEngine.Scene." + name, BinaryFieldKey.FromName(fields));
    private static void WriteVector(ref SaveWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z);
    }
    private static Vector3 ReadVector(ref SaveReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private struct OriginSerializer : IValueSerializer<WorldOrigin>
    {
        public void Write(ref SaveWriter writer, in WorldOrigin value)
        {
            writer.WriteDouble(value.X); writer.WriteDouble(value.Y); writer.WriteDouble(value.Z);
        }
        public void Read(ref SaveReader reader, out WorldOrigin value)
            => value = new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    private struct TransformSerializer : IValueSerializer<WorldTransform>
    {
        public void Write(ref SaveWriter writer, in WorldTransform value)
        {
            Quaternion rotation = value.Qvvs.Rotation;
            writer.WriteSingle(rotation.X); writer.WriteSingle(rotation.Y); writer.WriteSingle(rotation.Z); writer.WriteSingle(rotation.W);
            WriteVector(ref writer, value.Qvvs.Position); WriteVector(ref writer, value.Qvvs.Stretch); writer.WriteSingle(value.Qvvs.Scale);
        }
        public void Read(ref SaveReader reader, out WorldTransform value)
        {
            Quaternion rotation = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            value = new() { Qvvs = new TransformQvvs(ReadVector(ref reader), rotation) { Stretch = ReadVector(ref reader), Scale = reader.ReadSingle() } };
        }
    }
    private struct MeshSourceSerializer : IValueSerializer<MeshSource>
    {
        public void Write(ref SaveWriter writer, in MeshSource value)
        {
            writer.WriteExternalReference(new(value.Mesh.Value)); writer.WriteSingle(value.BoundsExpansion);
            writer.WriteUInt32((uint)value.DepthCacheClass); writer.WriteUInt32((uint)value.FirstPerson.Kind);
            writer.WriteUInt32(value.FirstPerson.PlayerId); writer.WriteUInt32((uint)value.FirstPerson.PlayerVisibility);
        }
        public void Read(ref SaveReader reader, out MeshSource value)
            => value = new()
            {
                Mesh = new(reader.ReadExternalReference().Value), BoundsExpansion = reader.ReadSingle(),
                DepthCacheClass = (DepthCacheClass)reader.ReadUInt32(),
                FirstPerson = new((FirstPersonMeshKind)reader.ReadUInt32(), reader.ReadUInt32(),
                    (MeshPlayerVisibility)reader.ReadUInt32()),
            };
    }
    private struct SkeletonSourceSerializer : IValueSerializer<SkeletonSource>
    {
        public void Write(ref SaveWriter writer, in SkeletonSource value)
            => writer.WriteExternalReference(new(value.Skeleton.Value));
        public void Read(ref SaveReader reader, out SkeletonSource value)
            => value = new() { Skeleton = new(reader.ReadExternalReference().Value) };
    }
    private struct XraySerializer : IValueSerializer<Xray>
    {
        public void Write(ref SaveWriter writer, in Xray value)
        {
            WriteVector(ref writer, value.Visible.ColorMultiplier); writer.WriteSingle(value.Visible.Opacity);
            WriteVector(ref writer, value.Occluded.ColorMultiplier); writer.WriteSingle(value.Occluded.Opacity);
            writer.WriteSingle(value.MinimumOcclusion); writer.WriteSingle(value.MaximumOcclusion);
        }

        public void Read(ref SaveReader reader, out Xray value) => value = new(
            new XrayBlend(ReadVector(ref reader), reader.ReadSingle()),
            new XrayBlend(ReadVector(ref reader), reader.ReadSingle()), reader.ReadSingle(), reader.ReadSingle());
    }

    private struct MotionSerializer : IValueSerializer<MeshMotion>
    {
        public void Write(ref SaveWriter writer, in MeshMotion value) { WriteVector(ref writer, value.Amplitude); writer.WriteUInt32(value.Seed); }
        public void Read(ref SaveReader reader, out MeshMotion value) => value = new() { Amplitude = ReadVector(ref reader), Seed = reader.ReadUInt32() };
    }
    private struct CameraSerializer : IValueSerializer<SceneCamera>
    {
        public void Write(ref SaveWriter writer, in SceneCamera value)
        {
            WriteVector(ref writer, value.Position); WriteVector(ref writer, value.Target); WriteVector(ref writer, value.Up);
            writer.WriteSingle(value.VerticalFieldOfView); writer.WriteSingle(value.NearPlane); writer.WriteSingle(value.FarPlane);
        }
        public void Read(ref SaveReader reader, out SceneCamera value) => value = new()
        {
            Position = ReadVector(ref reader), Target = ReadVector(ref reader), Up = ReadVector(ref reader),
            VerticalFieldOfView = reader.ReadSingle(), NearPlane = reader.ReadSingle(), FarPlane = reader.ReadSingle(),
        };
    }
    private struct DirectionalSerializer : IValueSerializer<DirectionalLight>
    {
        public void Write(ref SaveWriter writer, in DirectionalLight value)
        {
            WriteVector(ref writer, value.Direction);
            WriteVector(ref writer, value.Color);
            writer.WriteSingle(value.Intensity);
            writer.WriteUInt32(value.LayerMask);
        }
        public void Read(ref SaveReader reader, out DirectionalLight value) => value = new(
            ReadVector(ref reader), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
    private struct PointSerializer : IValueSerializer<PointLight>
    {
        public void Write(ref SaveWriter writer, in PointLight value)
        {
            WriteVector(ref writer, value.Position);
            writer.WriteSingle(value.Range);
            WriteVector(ref writer, value.Color);
            writer.WriteSingle(value.Intensity);
            writer.WriteUInt32(value.LayerMask);
        }
        public void Read(ref SaveReader reader, out PointLight value) => value = new(
            ReadVector(ref reader), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
    private struct SpotSerializer : IValueSerializer<SpotLight>
    {
        public void Write(ref SaveWriter writer, in SpotLight value)
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
        public void Read(ref SaveReader reader, out SpotLight value) => value = new(
            ReadVector(ref reader), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadSingle(), ReadVector(ref reader), reader.ReadSingle(), reader.ReadUInt32());
    }
}
