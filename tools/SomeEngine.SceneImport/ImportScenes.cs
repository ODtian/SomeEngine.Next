using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

if (args.Length != 4)
    throw new ArgumentException("Usage: SomeEngine.SceneImport <built-app-directory> <gltf-or-glb> <output-directory> <engine-directory>");
Game.Assets.MaterialTypes.Register();
ImportedScenes.SceneMaterialAsset.Register();
string application = Path.GetFullPath(args[0]);
string input = Path.GetFullPath(args[1]);
string output = Path.GetFullPath(args[2]);
string engine = Path.GetFullPath(args[3]);
string name = Path.GetFileNameWithoutExtension(input);
string copiedInput = Path.Combine(output, "assets", "Imported", name, Path.GetFileName(input));
if (!File.Exists(Path.Combine(output, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
{
    if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        throw new ArgumentException("The output must be empty or a previously generated scene application.");
    CopyFiles(Path.Combine(application, "assets"), Path.Combine(output, "assets"));
    CopyFiles(Path.Combine(application, "Library", "AssetManifest"), Path.Combine(output, "Library", "AssetManifest"));
}
CopyFiles(Path.GetDirectoryName(input)!, Path.GetDirectoryName(copiedInput)!);
using var importPages = new PageArena(new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 8L * 1024 * 1024 * 1024 }));
var materialConversion = new SceneMaterials();
var importer = new GltfSourceImporter(services =>
{
    Game.Importing.ProjectAssetCapabilities.AddAssetCapabilities(services);
    services.AddSingleton<IGltfMaterialConversion>(materialConversion);
    services.AddSingleton<IClusterVertexEncoder, SceneVertexEncoder>();
}, "imported-scenes-2;" + Game.Importing.ProjectAssetCapabilities.BuildVersion, importPages);
var project = new AssetProject(output, [importer], sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets/Shaders")])]);
// Imported geometry and materials remain scene content; refresh shared engine shaders
// on every build so scene directories cannot retain an older renderer interface.
AssetManifest applicationAssets = AssetManifest.Load(Path.Combine(application, "Library", "AssetManifest"));
foreach (AssetEntry entry in applicationAssets.Assets.Values)
{
    if (!entry.Path.StartsWith("assets/Shaders/", StringComparison.OrdinalIgnoreCase) &&
        !entry.Path.StartsWith("assets/Pipelines/", StringComparison.OrdinalIgnoreCase)) continue;
    string destination = Path.Combine(output, entry.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(Path.Combine(application, entry.Path), destination, overwrite: true);
    project.RegisterAsset(entry);
}
var metadata = SourceMeta.GetOrCreate(copiedInput);
if (metadata.Importers.Count == 0)
{
    metadata.Importers.Add(new(GltfSourceImporter.SourceImporterId,
        JsonSerializer.SerializeToElement(new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings)));
    SourceMeta.Save(copiedInput, metadata);
}
Console.WriteLine($"Importing {name}: {copiedInput}");
var model = ModelRoot.Load(copiedInput);
foreach (var material in model.LogicalMaterials)
    foreach (var channel in material.Channels)
        if (channel.Texture is not null && channel.TextureCoordinate != 0)
            throw new NotSupportedException($"Material {material.Name} uses UV{channel.TextureCoordinate}; this sample shader consumes UV0.");
await materialConversion.BuildAsync(project, model, engine);
var timer = Stopwatch.StartNew();
await project.ImportAsync(copiedInput);
double importSeconds = timer.Elapsed.TotalSeconds;
if (importPages.Residency.Used(ResidencyClass.DecodedCpu) != 0)
    throw new InvalidDataException("Import retained decoded output pages after completing the transaction.");
metadata = SourceMeta.Load(copiedInput);
Console.WriteLine($"Imported {metadata.Outputs.Count} assets in {importSeconds:F2}s");
var outputVersions = metadata.Outputs.ToDictionary(x => x.AssetGuid, x => (x.Version, x.ContentHash));
timer.Restart();
await project.ImportAsync(copiedInput);
double cachedSeconds = timer.Elapsed.TotalSeconds;
var repeated = SourceMeta.Load(copiedInput);
if (repeated.Outputs.Count != outputVersions.Count || repeated.Outputs.Any(x => !outputVersions.TryGetValue(x.AssetGuid, out var value) || value != (x.Version, x.ContentHash)))
    throw new InvalidDataException("Unchanged reimport changed generated assets.");
Console.WriteLine($"Unchanged reimport verified in {cachedSeconds:F2}s");
await using var loader = new AssetLoader(project.CreateStorage());
var meshes = new Dictionary<int, (Guid Id, AssetId<MaterialAsset>[] Materials)>();
foreach (var mesh in model.LogicalMeshes)
{
    string key = mesh.Extras?["someengine_id"]?.GetValue<string>() is { Length: > 0 } id
        ? $"mesh:id:{id}" : $"mesh:index:{mesh.LogicalIndex}";
    Guid meshId = metadata.ElementIds[key];
    using var imported = await MeshAsset.OpenAsync(loader, new AssetId<MeshAsset>(meshId));
    var materials = imported.GetBlock<MeshMaterialsBlock>().Materials.ToArray();
    if (materials.Length != mesh.Primitives.Count || materials.Any(x => x.IsEmpty))
        throw new InvalidDataException($"Mesh {mesh.Name} has incomplete material bindings.");
    meshes.Add(mesh.LogicalIndex, (meshId, materials));
}
using var world = new World();
int instances = 0;
var nodes = new List<object>();
foreach (var node in Node.Flatten(model.DefaultScene))
{
    nodes.Add(new { index = node.LogicalIndex, node.Name, parent = node.VisualParent?.LogicalIndex, mesh = node.Mesh?.LogicalIndex });
    if (node.Mesh is null) continue;
    if (!Matrix4x4.Decompose(node.WorldMatrix, out var scale, out var rotation, out var position))
        throw new InvalidDataException($"Node {node.Name} has an unsupported world transform.");
    var mesh = meshes[node.Mesh.LogicalIndex];
    var entity = world.CreateEntity(new WorldTransform { Qvvs = new(position, rotation) { Stretch = scale } });
    world.Add(entity, new MeshSource { Mesh = new(mesh.Id) });
    world.AddBuffer<MaterialSource>(entity);
    var references = mesh.Materials;
    world.ExecuteBufferWrite<MaterialSource, AssetId<MaterialAsset>[]>(entity, ref references,
        static (DynamicBuffer<MaterialSource> buffer, ref AssetId<MaterialAsset>[] values) =>
        {
            foreach (var material in values) buffer.Add(new(material));
        });
    instances++;
}
world.CreateEntity(new SceneCamera
{
    Position = name.Equals("Sponza", StringComparison.OrdinalIgnoreCase) ? new(-9, 2, 0) : new(-8, 3, 0),
    Target = name.Equals("Sponza", StringComparison.OrdinalIgnoreCase) ? new(5, 2, 0) : new(0, 2, 0),
    Up = Vector3.UnitY, VerticalFieldOfView = MathF.PI / 3, NearPlane = 0.05f, FarPlane = 2000,
});
world.CreateEntity(new DirectionalLight(new(0.35f, -0.85f, 0.4f), new(1, 0.96f, 0.9f), 3));
if (name.Equals("Sponza", StringComparison.OrdinalIgnoreCase))
    world.CreateEntity(new PointLight(new(0, 4, 0), 30, new(1, 0.95f, 0.9f), 15));
Guid sceneId = project.Resolve("assets/Scenes/default.scene.asset")!.Value;
using var scene = new WorldAsset(PageArena.Shared, sceneId) { Name = name };
scene.GetBlock<WorldBlock>().WriteWorld(world, SceneSerialization.CreateRegistry());
project.RegisterAsset(await scene.SaveAsync(Path.Combine(output, "assets/Scenes/default.scene.asset")));
File.WriteAllText(Path.Combine(output, "scene-import.json"), JsonSerializer.Serialize(new
{
    source = input, meshes = meshes.Count, instances, materials = model.LogicalMaterials.Count,
    textures = metadata.Outputs.Count(x => x.SubAssetKey.StartsWith("texture:")),
    assets = metadata.Outputs.Count, importSeconds, cachedSeconds, unchangedReimport = true, nodes,
    displayLimitations = model.LogicalMaterials
        .Where(material => material.DoubleSided || material.Alpha == AlphaMode.BLEND)
        .Select(material => new { material.Name, doubleSided = material.DoubleSided, alphaMode = material.Alpha.ToString() }),
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Scene saved: {instances} mesh instances, {model.LogicalMaterials.Count} materials. {output}");

static void CopyFiles(string source, string destination)
{
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            using var original = File.OpenRead(file);
            using var existing = File.OpenRead(target);
            if (original.Length == existing.Length && SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(existing))) continue;
        }
        File.Copy(file, target, overwrite: true);
    }
}

// The scene's material shaders consume UV0. Extra UV sets are retained in the
// original input but are not part of this project's GPU vertex representation.
sealed class SceneVertexEncoder : IClusterVertexEncoder
{
    public string Compatibility => StandardClusterVertexEncoder.EncodingCompatibility;
    public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions,
        IReadOnlyList<RawAttribute> attributes, ReadOnlySpan<uint> clusterVertices)
        => StandardClusterVertexEncoder.Instance.Encode(positions,
            attributes.Where(value => !value.Name.StartsWith("TEXCOORD_") || value.Name == "TEXCOORD_0").ToArray(), clusterVertices);
}
