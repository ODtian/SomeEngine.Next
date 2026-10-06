using System.Numerics;
using Game.Assets;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Cluster.Tests;

internal sealed record ShadowReferenceInputs(int Width, int Height, SceneCamera Camera,
    ShadowReferenceScene.MeshPlacement[] Meshes, Action<World>? AddLights);

// Mechanical content serialization only. Tests author placements, lights and actions.
internal static class ShadowReferenceScene
{
    internal const int Width = 256;
    internal const int Height = 256;
    internal static readonly Vector3 ReceiverPosition = new(0, 0, -8);
    internal static readonly Vector3 ReceiverScale = new(4, 4, .1f);
    internal static readonly Vector3 CasterPosition = new(0, 0, -6);
    internal static readonly Vector3 CasterScale = new(.6f);
    internal static readonly Vector3 PointPosition = new(-4, 2, -3);
    internal static readonly Vector3 LightDirection = Vector3.Normalize(new(.35f, -.85f, -.4f));
    internal readonly record struct MeshPlacement(Vector3 Position, Vector3 Scale, bool Receiver, MeshMotion? Motion = null);

    internal static async Task BuildAsync(string engine, string content, ShadowReferenceInputs inputs)
    {
        string source = Path.Combine(engine, "templates", "DefaultProject");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets"), "*.asset", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(source, "Library", "AssetManifest"), "*", SearchOption.AllDirectories)))
        {
            string destination = Path.Combine(content, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        MaterialTypes.Register();
        var project = new AssetProject(content, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        AssetEntry meshEntry = project.Manifest.List(MeshAsset.FileType).Single();
        using MeshAsset mesh = await MeshAsset.OpenAsync(loader, new(meshEntry.AssetGuid));
        AssetEntry material = project.Manifest.List(MaterialAsset.FileType).Single(item => item.Name == "Standard");
        mesh.GetBlock<MeshMaterialsBlock>().Materials = [new(material.AssetGuid)];
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(content, meshEntry.Path)));
        AssetEntry settingsEntry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(loader, new(settingsEntry.AssetGuid));
        ApplicationSettingsBlock values = settings.GetBlock<ApplicationSettingsBlock>();
        values.Width = inputs.Width;
        values.Height = inputs.Height;
        project.RegisterAsset(await settings.SaveAsync(Path.Combine(content, settingsEntry.Path)));
        await WriteWorldAsync(content, inputs);
    }

    internal static async Task WriteWorldAsync(string content, ShadowReferenceInputs inputs)
    {
        var project = new AssetProject(content, []);
        AssetEntry mesh = project.Manifest.List(MeshAsset.FileType).Single();
        AssetEntry scene = project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
        using var world = new World();
        foreach (MeshPlacement placement in inputs.Meshes)
        {
            var entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new TransformQvvs(placement.Position, Quaternion.Identity) { Stretch = placement.Scale },
            });
            world.Add(entity, new MeshSource { Mesh = new(mesh.AssetGuid) });
            if (placement.Motion is MeshMotion motion) world.Add(entity, motion);
        }
        world.CreateEntity(inputs.Camera);
        inputs.AddLights?.Invoke(world);
        using var asset = new WorldAsset(scene.AssetGuid) { Name = scene.Name };
        asset.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await asset.SaveAsync(Path.Combine(content, scene.Path)));
    }
}
