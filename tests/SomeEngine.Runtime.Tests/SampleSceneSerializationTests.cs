using System.Numerics;
using DefaultGame;
using SomeEngine.Assets;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Serialization;

namespace SomeEngine.Runtime.Tests;

public sealed class SampleSceneSerializationTests
{
    [Fact]
    public async Task ExistingSavedMotionLoadsIntoTheSampleComponent()
    {
        Guid meshId = Guid.NewGuid();
        using var world = new World();
        var entity = world.CreateEntity(new LegacyMeshMotion { Amplitude = new(1.25f, -2.5f, 3.75f), Seed = uint.MaxValue });
        world.Add(entity, new MeshSource { Mesh = new(meshId), BoundsExpansion = 0.375f });
        using var asset = new WorldAsset();
        asset.WriteWorld(world, CreateLegacyRegistry());

        using World loaded = await asset.LoadAsync(SampleSceneSerialization.CreateRegistry());

        Assert.Equal(new Vector3(1.25f, -2.5f, 3.75f), loaded.Read<MeshMotion>(entity).Amplitude);
        Assert.Equal(uint.MaxValue, loaded.Read<MeshMotion>(entity).Seed);
        Assert.Equal(meshId, loaded.Read<MeshSource>(entity).Mesh.Value);
        Assert.Equal(0.375f, loaded.Read<MeshSource>(entity).BoundsExpansion);
    }

    [Fact]
    public async Task SavedSampleMotionKeepsTheExistingSerializedType()
    {
        Guid meshId = Guid.NewGuid();
        using var world = new World();
        var entity = world.CreateEntity(new MeshMotion { Amplitude = new(-4.5f, 5.25f, 6.75f), Seed = 0x9E3779B9u });
        world.Add(entity, new MeshSource { Mesh = new(meshId), BoundsExpansion = 0.625f });
        SerializationRegistry registry = SceneSerialization.CreateRegistry();
        SampleSceneSerialization.Register(registry);
        using var asset = new WorldAsset();
        asset.WriteWorld(world, registry);

        using World loaded = await asset.LoadAsync(CreateLegacyRegistry());

        Assert.Equal(new Vector3(-4.5f, 5.25f, 6.75f), loaded.Read<LegacyMeshMotion>(entity).Amplitude);
        Assert.Equal(0x9E3779B9u, loaded.Read<LegacyMeshMotion>(entity).Seed);
        Assert.Equal(meshId, loaded.Read<MeshSource>(entity).Mesh.Value);
        Assert.Equal(0.625f, loaded.Read<MeshSource>(entity).BoundsExpansion);
    }

    private static SerializationRegistry CreateLegacyRegistry() => SceneSerialization.CreateRegistry()
        .Register<LegacyMeshMotion, LegacyMotionCodec>(new(
            BinaryTypeId.FromLogicalName("SomeEngine.Scene.MeshMotion"), "SomeEngine.Scene.MeshMotion",
            BinaryFieldKey.FromName("amplitude:3f;seed:u32")));

    private struct LegacyMeshMotion : IComponent
    {
        public Vector3 Amplitude;
        public uint Seed;
    }

    private struct LegacyMotionCodec : ICanonicalComponentCodec<LegacyMeshMotion>
    {
        public void Write(ref DataWriter writer, in LegacyMeshMotion value)
        {
            writer.WriteSingle(value.Amplitude.X); writer.WriteSingle(value.Amplitude.Y); writer.WriteSingle(value.Amplitude.Z);
            writer.WriteUInt32(value.Seed);
        }

        public void Read(ref DataReader reader, out LegacyMeshMotion value) => value = new()
        {
            Amplitude = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            Seed = reader.ReadUInt32(),
        };
    }
}
