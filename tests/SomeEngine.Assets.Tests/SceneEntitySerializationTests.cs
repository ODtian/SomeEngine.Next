using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Tests.Assets;

public sealed class SceneEntitySerializationTests
{
    [Fact]
    public async Task SceneEntityEncodingRetainsMeshReferenceDepthAndPlayerVisibility()
    {
        using var source = new World();
        var mesh = new AssetId<MeshAsset>(Guid.NewGuid());
        var data = new MeshSource
        {
            Mesh = mesh, BoundsExpansion = 3,
            DepthCacheClass = DepthCacheClass.Static,
            FirstPerson = new(FirstPersonMeshKind.WorldRepresentation, 7, MeshPlayerVisibility.OtherPlayers),
        };
        Entity entity = source.Create(data);
        using var block = new EntityBlock();
        block.WriteEntities(source, [entity], new(Codecs: SceneSerialization.Codecs));
        ReadOnlyMemory<byte> bytes = await block.ReadBytesAsync(EntityBlock.ContentKey);

        using var target = new World();
        Entity restored = Assert.Single(WorldSerializer.ReadEntities(bytes, target,
            new SerializationReadOptions(Codecs: SceneSerialization.Codecs)));
        MeshSource value = target.ReadComponentData<MeshSource>(restored);
        Assert.Equal(data.Mesh, value.Mesh);
        Assert.Equal(data.BoundsExpansion, value.BoundsExpansion);
        Assert.Equal(data.DepthCacheClass, value.DepthCacheClass);
        Assert.Equal(data.FirstPerson, value.FirstPerson);
        Assert.Contains(new AssetId<Asset>(mesh.Value), block.ExternalReferences);
    }
}
