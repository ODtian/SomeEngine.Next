using SomeEngine.ECS;
using SomeEngine.Geometry.Cluster;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.SceneContent;

public static partial class BehaviorScenes
{
    /// <summary>Textured intersecting Standard and Unlit panels with a point-light shadow caster.</summary>
    public static async Task CreateLitTransparencyAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        using var standard = await StandardMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "Standard" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        using var green = await UnlitMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "green sphere" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        using var sphere = await MeshAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "red sphere" && entry.AssetType == MeshAsset.FileType).AssetGuid));

        byte[][] checkerColors = [[255, 48, 24, 255], [255, 208, 96, 255]];
        byte[] checkerPixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            checkerColors[(x / 2 + y / 2) & 1].CopyTo(checkerPixels, (y * 8 + x) * 4);
        AssetId<TextureAsset> checker = await SaveTextureAsync("lit-panel-checker", 8, 8, checkerPixels);
        AssetId<TextureAsset> normal = await SaveTextureAsync("lit-panel-normal", 2, 2,
        [
            191, 153, 235, 255, 83, 185, 232, 255,
            166, 64, 231, 255, 70, 108, 240, 255,
        ]);

        using var litPanel = standard.CreateCopy();
        litPanel.Name = "lit panel";
        _ = new StandardMaterialParameters(litPanel.GetBlock<MaterialBlock>())
        {
            AlbedoMap = checker,
            NormalMap = normal,
            BaseColorTint = new(1, .4f, .4f, .5f),
            MetallicFactor = 0,
            Roughness = .5f,
            EmissiveFactor = Vector3.Zero,
        };
        project.RegisterAsset(await litPanel.SaveAsync(Path.Combine(destination, "assets/Materials/lit-panel.material.asset")));
        using var unlitPanel = green.CreateCopy();
        unlitPanel.Name = "unlit panel";
        new UnlitMaterialParameters(unlitPanel.GetBlock<MaterialBlock>()).BaseColorTint = new(0, 1, 0, .5f);
        project.RegisterAsset(await unlitPanel.SaveAsync(Path.Combine(destination, "assets/Materials/unlit-panel.material.asset")));
        using var casterMaterial = standard.CreateCopy();
        casterMaterial.Name = "shadow caster";
        new StandardMaterialParameters(casterMaterial.GetBlock<MaterialBlock>()).BaseColorTint = new(.8f, .8f, .8f, 1);
        project.RegisterAsset(await casterMaterial.SaveAsync(Path.Combine(destination, "assets/Materials/shadow-caster.material.asset")));

        using var litMesh = ClusterBuilder.ProcessRaw(
            [new(-2, -2, -1), new(2, -2, 1), new(2, 2, 1), new(-2, 2, -1)],
            PanelAttributes(.5f), [0, 2, 1, 0, 3, 2], [new(litPanel.Id)], "lit panel");
        using var unlitMesh = ClusterBuilder.ProcessRaw(
            [new(-2, -2, 1), new(2, -2, -1), new(2, 2, -1), new(-2, 2, 1)],
            PanelAttributes(-.5f), [0, 2, 1, 0, 3, 2], [new(unlitPanel.Id)], "unlit panel");
        using var casterMesh = sphere.CreateCopy();
        casterMesh.Name = "shadow caster";
        casterMesh.GetBlock<MeshMaterialsBlock>().Materials = [casterMaterial.Reference];
        project.RegisterAsset(await litMesh.SaveAsync(Path.Combine(destination, "assets/Meshes/lit-panel.mesh.asset")));
        project.RegisterAsset(await unlitMesh.SaveAsync(Path.Combine(destination, "assets/Meshes/unlit-panel.mesh.asset")));
        project.RegisterAsset(await casterMesh.SaveAsync(Path.Combine(destination, "assets/Meshes/shadow-caster.mesh.asset")));

        using var world = new World();
        Entity litEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
        world.Add(litEntity, new MeshSource { Mesh = litMesh.Reference });
        Entity unlitEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity) });
        world.Add(unlitEntity, new MeshSource { Mesh = unlitMesh.Reference });
        Entity casterEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(-1, 1, 3.5f), Quaternion.Identity, .4f) });
        world.Add(casterEntity, new MeshSource { Mesh = casterMesh.Reference });
        world.CreateEntity(new PointLight(new(-2, 2, 1), 20, Vector3.One, 40));
        await WriteSceneAsync(project, world);
        await SceneContent.SetOutputSizeAsync(destination, 128, 128);

        async Task<AssetId<TextureAsset>> SaveTextureAsync(string name, uint width, uint height, byte[] rgba)
        {
            using var pixels = new TextureBlock
            {
                Dimension = TextureDimension.Texture2D,
                Width = width, Height = height, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
                Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm,
                SampledDimension = TextureViewDimension.Texture2D,
            };
            ulong key = TextureTiles.ChunkKey(0, 0, 0);
            pixels.MipTiles.Add(new TextureMipTile
            {
                Width = width, Height = height, RowPitch = width * 4, SlicePitch = width * height * 4,
                ChunkKey = key, DecodedLength = (ulong)rgba.Length,
            });
            pixels.SetBytes(key, rgba);
            using var texture = new TextureAsset { Name = name };
            texture.SetBlock(pixels);
            project.RegisterAsset(await texture.SaveAsync(Path.Combine(destination, $"assets/Textures/{name}.texture.asset")));
            return texture.Reference;
        }

        static List<RawAttribute> PanelAttributes(float slope)
        {
            Vector3 panelNormal = Vector3.Normalize(new Vector3(slope, 0, -1));
            Vector3 panelTangent = Vector3.Normalize(new Vector3(1, 0, slope));
            float[] normals = new float[4 * 3];
            float[] tangents = new float[4 * 4];
            for (int vertex = 0; vertex < 4; vertex++)
            {
                normals[vertex * 3] = panelNormal.X;
                normals[vertex * 3 + 2] = panelNormal.Z;
                tangents[vertex * 4] = panelTangent.X;
                tangents[vertex * 4 + 2] = panelTangent.Z;
                tangents[vertex * 4 + 3] = -1;
            }
            return
            [
                new("NORMAL", normals, 3, GeometryValueType.Int8, 3, true),
                new("TANGENT", tangents, 4, GeometryValueType.Int8, 4, true),
                new("TEXCOORD_0", new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, GeometryValueType.Float16, 2, false),
            ];
        }
    }
}
