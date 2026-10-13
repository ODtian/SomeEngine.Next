using System.Numerics;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Serialization.Containers;
using SharpGLTF.Schema2;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Importers;

public sealed record GltfSceneSettings;

public sealed class GltfSceneSettingsProvider : IAssetImporterSettings<GltfSceneSettings>
{
    public GltfSceneSettings DefaultSettings => new();
    public JsonTypeInfo<GltfSceneSettings> TypeInfo => GltfSceneSettingsJson.Default.GltfSceneSettings;
}

[JsonSerializable(typeof(GltfSceneSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public sealed partial class GltfSceneSettingsJson : JsonSerializerContext;

/// <summary>Provides glTF node and mesh assembly with extensible material creation.</summary>
public abstract class GltfSceneImporter : IAssetImporter<Scene, GltfSceneSettings>
{
    public virtual async ValueTask ImportMeshMaterialsAsync(GltfMesh source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        var references = new MeshMaterialsBlock();
        try
        {
            foreach (var primitive in source.Primitives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetId<MaterialAsset> material = default;
                if (primitive.Material is not null)
                {
                    MaterialAsset imported = await context.GetAssetAsync<GltfMaterial, MaterialAsset>(
                        primitive.Material, cancellationToken).ConfigureAwait(false);
                    material = imported.Reference;
                }
                references.Materials.Add(material);
            }
            output.SetBlock(references);
        }
        catch { references.Dispose(); throw; }
    }

    public abstract ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken);

    public virtual async ValueTask ImportAsync(Scene source, GltfSceneSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        using var world = new World();
        await AddNodesAsync(world, source, context, cancellationToken).ConfigureAwait(false);
        await AddSceneContentAsync(world, source, context, cancellationToken).ConfigureAwait(false);
        var block = new WorldBlock();
        try
        {
            block.WriteWorld(world, SceneSerialization.CreateRegistry());
            output.SetBlock(block);
        }
        catch { block.Dispose(); throw; }
    }

    protected virtual async ValueTask AddNodesAsync(World world, Scene scene, ImportContext context,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<Node, Entity>();
        foreach (Node node in Node.Flatten(scene))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matrix4x4.Decompose(node.WorldMatrix, out Vector3 scale, out Quaternion rotation, out Vector3 position))
                throw new InvalidDataException($"Node {node.Name} has a transform that cannot be represented by the Game world.");
            var entity = world.CreateEntity(new WorldTransform { Qvvs = new(position, rotation) { Stretch = scale } });
            nodes.Add(node, entity);
            if (node.Mesh is GltfMesh mesh)
            {
                MeshAsset imported = await context.GetAssetAsync<GltfMesh, MeshAsset>(mesh, cancellationToken).ConfigureAwait(false);
                world.Add(entity, new MeshSource { Mesh = imported.Reference });
            }
        }
        foreach ((Node node, Entity entity) in nodes)
            if (node.VisualParent is Node parent)
                Hierarchy.SetParent(world, entity, nodes[parent]);
    }

    /// <summary>Projects may add cameras, lights, or procedural instances after glTF nodes are assembled.</summary>
    protected virtual ValueTask AddSceneContentAsync(World world, Scene scene, ImportContext context,
        CancellationToken cancellationToken)
    {
        world.CreateEntity(new SceneCamera
        {
            Position = new(0, 2, -8), Target = Vector3.Zero, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = 0.05f, FarPlane = 2000,
        });
        world.CreateEntity(new DirectionalLight(new(0.35f, -0.85f, 0.4f), new(1, 0.96f, 0.9f), 3));
        return ValueTask.CompletedTask;
    }
}
