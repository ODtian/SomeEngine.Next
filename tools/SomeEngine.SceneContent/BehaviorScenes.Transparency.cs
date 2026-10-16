using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.SceneContent;

public static partial class BehaviorScenes
{
    /// <summary>Two intersecting planes whose nearest surface changes across the image.</summary>
    public static async Task CreateIntersectingPanelsAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        using var red = await UnlitMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "red sphere" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        using var green = await UnlitMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "green sphere" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        List<RawAttribute> attributes =
        [
            new("NORMAL", new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 }, 3, GeometryValueType.Int8, 3, true),
            new("TEXCOORD_0", new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, GeometryValueType.Float16, 2, false),
        ];
        using var first = ClusterBuilder.ProcessRaw(
            [new(-2, -2, -1), new(2, -2, 1), new(2, 2, 1), new(-2, 2, -1)],
            attributes, [0, 2, 1, 0, 3, 2], [new(red.Id)], "red panel");
        using var second = ClusterBuilder.ProcessRaw(
            [new(-2, -2, 1), new(2, -2, -1), new(2, 2, -1), new(-2, 2, 1)],
            attributes, [0, 2, 1, 0, 3, 2], [new(green.Id)], "green panel");
        project.RegisterAsset(await first.SaveAsync(Path.Combine(destination, "assets/Meshes/red-panel.mesh.asset")));
        project.RegisterAsset(await second.SaveAsync(Path.Combine(destination, "assets/Meshes/green-panel.mesh.asset")));
        await WriteSceneAsync(project, world =>
        {
            Entity redEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
            world.Add(redEntity, new MeshSource { Mesh = first.Reference });
            Entity greenEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
            world.Add(greenEntity, new MeshSource { Mesh = second.Reference });
        });
        await SceneContent.SetOutputSizeAsync(destination, 64, 64);
    }
}
