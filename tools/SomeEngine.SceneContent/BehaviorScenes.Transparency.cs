using SomeEngine.ECS;
using SomeEngine.Geometry.Cluster;
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
    public static async Task CreateOffsetPanelsAsync(string source, string destination, string engineRoot)
    {
        CopyContent(source, destination);
        string shader = File.ReadAllText(Path.Combine(destination, "assets/Shaders/unlit.slang"))
            .Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;", """
                [VisibilityDepth("offset")]
                [MaterialDepthInputs("")]
                struct OffsetVisibility : IVisibilityEvaluate<VertexEvaluate.Vertex>
                {
                    typealias Varying = EmptyVisibilityVarying;
                    Varying makeVarying(VertexEvaluate.Vertex vertex, VisibilityVertexInput input) { return {}; }
                    Varying readLane(Varying value, uint lane) { return {}; }
                    Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
                    Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
                    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
                    {
                        VisibilityPixelResult result;
                        result.Visible = 1u;
                        result.Depth = .5;
                        return result;
                    }
                };
                typedef OffsetVisibility VisibilityEvaluate;
                """)
            .Replace("TRANSPARENCY_MATERIAL_TYPE UnlitTransparencyMaterial", "TRANSPARENCY_MATERIAL_TYPE OffsetUnlitTransparencyMaterial");
        await SetGreenShaderAsync(destination, engineRoot, "offset-unlit", shader);
    }

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
        using var world = new World();
        Entity redEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
        world.Add(redEntity, new MeshSource { Mesh = first.Reference });
        Entity greenEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
        world.Add(greenEntity, new MeshSource { Mesh = second.Reference });
        await WriteSceneAsync(project, world);
        await SceneContent.SetOutputSizeAsync(destination, 64, 64);
    }
}
