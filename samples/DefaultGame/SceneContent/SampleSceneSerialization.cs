using System.Numerics;
using SomeEngine.ECS.Serialization;
using SomeEngine.Render;
using SomeEngine.Serialization;

namespace DefaultGame;

/// <summary>Adds the sample's components to the shared scene serialization.</summary>
public static class SampleSceneSerialization
{
    public static SerializationRegistry CreateRegistry()
    {
        SerializationRegistry registry = SceneSerialization.CreateRegistry();
        Register(registry);
        return registry;
    }

    public static void Register(SerializationRegistry registry)
        => registry.Register<MeshMotion, MotionCodec>(new(
            BinaryTypeId.FromLogicalName("SomeEngine.Scene.MeshMotion"), "SomeEngine.Scene.MeshMotion",
            BinaryFieldKey.FromName("amplitude:3f;seed:u32")));

    private static void WriteVector(ref DataWriter writer, Vector3 value)
    {
        writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z);
    }
    private static Vector3 ReadVector(ref DataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private struct MotionCodec : ICanonicalComponentCodec<MeshMotion>
    {
        public void Write(ref DataWriter writer, in MeshMotion value) { WriteVector(ref writer, value.Amplitude); writer.WriteUInt32(value.Seed); }
        public void Read(ref DataReader reader, out MeshMotion value) => value = new() { Amplitude = ReadVector(ref reader), Seed = reader.ReadUInt32() };
    }
}
