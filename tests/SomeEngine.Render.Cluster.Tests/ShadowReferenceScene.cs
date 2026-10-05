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
    internal static bool IsPointLight(string light) => light.StartsWith("point", StringComparison.Ordinal);
    internal static bool IsSpotLight(string light) => light.StartsWith("spot", StringComparison.Ordinal);
    // The caster footprint is about nine units from the point light; the receiver
    // extends beyond 9.3. Keep shadow, light and range-boundary samples together.
    internal static float LocalRange(string light) => light == "point-range" ? 9.3f : UsesDefaultGrid(light) ? 28 : 20;
    // Retained RGBA16F controls need direct radiance above the ambient quantization
    // step near the finite range boundary. Intensity cancels in the visibility ratio.
    internal static float LocalIntensity(string light) => light == "point-range" ? 300 : 30;
    internal static Vector3 SpotDirection => Vector3.Normalize(ReceiverPosition - PointPosition);
    internal static float OuterConeCos(string light) => MathF.Cos(light == "spot-cone" ? MathF.PI / 9 : MathF.PI * 2 / 9);
    internal static bool HasMotion(string light) => light.EndsWith("-motion", StringComparison.Ordinal);
    internal static bool UsesSoftwareRaster(string light) => light.EndsWith("-software", StringComparison.Ordinal);
    internal static bool UsesDefaultGrid(string light) => light.EndsWith("-grid", StringComparison.Ordinal);
    internal static int ImageWidth(string light) => UsesDefaultGrid(light) ? 1280 : Width;
    internal static int ImageHeight(string light) => UsesDefaultGrid(light) ? 720 : Height;
    internal static Vector3 CameraPosition(string light) => UsesDefaultGrid(light) ? new(0, 0, -3) : Vector3.Zero;
    internal static float FieldOfView(string light) => UsesDefaultGrid(light) ? MathF.PI / 4 : 1;
    internal static float FarPlane(string light) => UsesDefaultGrid(light) ? 1000 : 100;
    internal static Vector3 Direction(string light) => UsesDefaultGrid(light)
        ? Vector3.Normalize(new(.35f, -.85f, .4f)) : LightDirection;
    internal static Vector3 PointLightPosition(string light) => UsesDefaultGrid(light) ? new(0, 6, 52) : PointPosition;
    internal static MeshPlacement[] Meshes(string light, bool captured)
    {
        if (UsesDefaultGrid(light)) return Enumerable.Range(0, 1024).Select(index => new MeshPlacement(
            new(-23.25f + index % 32 * 1.5f, -23.25f + index / 32 * 1.5f, 65), new(.6f), true, false)).ToArray();
        return [new(ReceiverPosition, ReceiverScale, true, false),
            new(captured ? CasterAtCapture(light) : CasterStart(light), CasterScale, false, HasMotion(light))];
    }
    internal readonly record struct MeshPlacement(Vector3 Position, Vector3 Scale, bool Receiver, bool Moving);
    // MeshMotion seed 0 reaches this documented pose at frame 63 of the fixed 1/60 s playback.
    internal static Vector3 CasterStart(string light) => light.EndsWith("-contact", StringComparison.Ordinal)
        ? new(0, 0, -7.25f) : CasterPosition;
    internal static Vector3 CasterAtCapture(string light) => CasterStart(light) +
        (HasMotion(light) ? new Vector3(.75f * MathF.Sin(63 * .016666667f * .73406994f), 0, 0) : Vector3.Zero);

    internal static object Description(string light) => new
    {
        Name = UsesDefaultGrid(light) ? "DefaultGame 1024 spheres with an isolated light" : "Icosphere caster and flattened receiver",
        Camera = new { Position = new[] { CameraPosition(light).X, CameraPosition(light).Y, CameraPosition(light).Z },
            Target = UsesDefaultGrid(light) ? new[] { 0f, 0f, 65f } : new[] { 0f, 0f, -8f },
            VerticalFieldOfView = FieldOfView(light), NearPlane = .1f, FarPlane = FarPlane(light) },
        Grid = UsesDefaultGrid(light) ? new { Count = 1024, Columns = 32, Spacing = 1.5f, Scale = .6f, Z = 65f } : null,
        Receiver = UsesDefaultGrid(light) ? null : new { Position = new[] { 0f, 0f, -8f }, Scale = new[] { 4f, 4f, .1f } },
        Caster = UsesDefaultGrid(light) ? null : new { Position = new[] { 0f, 0f, CasterStart(light).Z }, Scale = new[] { .6f, .6f, .6f },
            Moving = HasMotion(light), AmplitudeX = HasMotion(light) ? .75f : 0f, Seed = 0,
            CapturedPosition = new[] { CasterAtCapture(light).X, 0f, CasterAtCapture(light).Z } },
        Light = IsPointLight(light) || IsSpotLight(light)
            ? new { Type = light, Vector = new[] { PointLightPosition(light).X, PointLightPosition(light).Y, PointLightPosition(light).Z }, Intensity = LocalIntensity(light), Range = LocalRange(light) }
            : new { Type = light, Vector = new[] { Direction(light).X, Direction(light).Y, Direction(light).Z }, Intensity = 3f, Range = 0f },
        SpotCone = IsSpotLight(light) ? new { Direction = SpotDirection, InnerConeCos = MathF.Cos(MathF.PI / 12),
            OuterConeCos = OuterConeCos(light) } : null,
        Material = "Standard: white albedo; metallic=0; roughness=.5; emissive=0",
    };

    internal static async Task BuildAsync(string engine, string content, string light)
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
        values.Width = ImageWidth(light);
        values.Height = ImageHeight(light);
        project.RegisterAsset(await settings.SaveAsync(Path.Combine(content, settingsEntry.Path)));
        await WriteWorldAsync(content, light, illuminated: true);
    }

    internal static async Task WriteWorldAsync(string content, string light, bool illuminated)
    {
        var project = new AssetProject(content, []);
        AssetEntry mesh = project.Manifest.List(MeshAsset.FileType).Single();
        AssetEntry scene = project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
        using var world = new World();
        foreach (MeshPlacement placement in Meshes(light, captured: false))
            AddMesh(placement.Position, placement.Scale, placement.Moving);
        world.CreateEntity(new SceneCamera
        {
            Position = CameraPosition(light), Target = UsesDefaultGrid(light) ? new(0, 0, 65) : ReceiverPosition, Up = Vector3.UnitY,
            VerticalFieldOfView = FieldOfView(light), NearPlane = .1f, FarPlane = FarPlane(light),
        });
        if (illuminated)
        {
            if (IsPointLight(light)) world.CreateEntity(new PointLight(PointLightPosition(light), LocalRange(light), Vector3.One, LocalIntensity(light)));
            else if (IsSpotLight(light)) world.CreateEntity(new SpotLight(PointPosition, LocalRange(light),
                SpotDirection, MathF.Cos(MathF.PI / 12), OuterConeCos(light), Vector3.One, 30));
            else world.CreateEntity(new DirectionalLight(Direction(light), Vector3.One, 3));
        }
        using var asset = new WorldAsset(scene.AssetGuid) { Name = scene.Name };
        asset.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await asset.SaveAsync(Path.Combine(content, scene.Path)));

        void AddMesh(Vector3 position, Vector3 scale, bool moving)
        {
            var entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new TransformQvvs(position, Quaternion.Identity) { Stretch = scale },
            });
            world.Add(entity, new MeshSource { Mesh = new(mesh.AssetGuid) });
            if (moving) world.Add(entity, new MeshMotion { Amplitude = new(.75f, 0, 0), Seed = 0 });
        }
    }
}
