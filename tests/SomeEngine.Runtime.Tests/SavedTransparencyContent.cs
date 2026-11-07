using System.Numerics;
using System.Text.Json;
using DefaultGame;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Instances;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Packs;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Runtime.Tests;

public sealed class SavedTransparencyContent : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-SavedTransparency-{Guid.NewGuid():N}");
    private Guid _application;
    private string[] _packs = [];
    private string _engine = "";
    private Task<Shader>? _curvedShader;
    public Shader Complete { get; private set; } = null!;
    public Shader Unordered { get; private set; } = null!;
    public Mesh Panel { get; } = CreatePanel();
    public Mesh CurvedMesh { get; } = CreateCurvedMesh();
    public Texture White { get; } = CreateTexture([255, 255, 255, 255], 1);
    public Texture Checker { get; } = CreateTexture([
        255, 80, 40, 255, 40, 180, 255, 255,
        40, 180, 255, 255, 255, 80, 40, 255], 2);

    public ApplicationScene CreateApplication() => new(_directory)
    {
        ApplicationAsset = new(_application), AssetPacks = _packs,
        RegisterSceneComponents = SampleSceneSerialization.Register,
    };

    public Task<Shader> GetCurvedShaderAsync() => _curvedShader ??= CreateCurvedShaderAsync();

    public UnlitMaterial CreateMaterial(Shader shader, Vector4 color) => new()
    {
        Shader = shader, AlbedoMap = White, BaseColorTint = color,
        MaterialSampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat),
        State = MaterialState.Default with { TwoSided = true },
    };

    public async Task InitializeAsync()
    {
        string? engine = AppContext.BaseDirectory;
        while (engine is not null && !File.Exists(Path.Combine(engine, "SomeEngine.slnx"))) engine = Path.GetDirectoryName(engine);
        _engine = engine!;
        Directory.CreateDirectory(_directory);
        try
        {
            await CreateApplicationAsync(Environment.GetEnvironmentVariable("SOMEENGINE_TEST_COOKED_CONTENT") ??
                Path.Combine(engine!, "samples", "DefaultGame"));
            await CreateShadersAsync(engine!);
        }
        catch { await DisposeAsync(); throw; }
    }

    private async Task CreateApplicationAsync(string sourceDirectory)
    {
        var project = new AssetProject(_directory, []);
        await using AssetContent content = await AssetContent.OpenAsync(sourceDirectory);
        AssetEntry originalEntry = content.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        using ApplicationSettingsAsset original = await ApplicationSettingsAsset.OpenAsync(content.Loader, new(originalEntry.AssetGuid));
        using ApplicationSettingsAsset application = original.CreateCopy();
        using var world = new World();
        world.CreateEntity(new SceneCamera
        {
            Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = .1f, FarPlane = 100,
        });
        using var scene = new WorldAsset { Name = "Saved transparency panels" };
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        AssetEntry sceneEntry = await scene.SaveAsync(Path.Combine(_directory, "panels.scene.asset"));
        project.RegisterAsset(sceneEntry);
        ApplicationSettingsBlock settings = application.GetBlock<ApplicationSettingsBlock>();
        settings.Scene = scene.Reference;
        settings.Width = settings.Height = 64;
        AssetEntry applicationEntry = await application.SaveAsync(Path.Combine(_directory, "panels.runtime.asset"));
        project.RegisterAsset(applicationEntry);
        _application = application.Id;
        string pathsFile = Path.Combine(sourceDirectory, AssetContent.PackPathsFile);
        if (File.Exists(pathsFile))
        {
            string[] shared = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(pathsFile))!
                .Select(path => Path.GetFullPath(path, sourceDirectory)).ToArray();
            string overlay = Path.Combine(_directory, "panels.assetpack");
            var pack = new AssetPackBuilder();
            foreach (var (entry, file) in new[] { (sceneEntry, "panels.scene.asset"), (applicationEntry, "panels.runtime.asset") })
                pack.AddAsset(entry.AssetGuid, entry.AssetType, await File.ReadAllBytesAsync(Path.Combine(_directory, file)),
                    entry.SchemaFingerprint, ChunkCompression.None);
            await pack.PublishAsync(overlay);
            _packs = [overlay, .. shared];
            return;
        }
        var source = new AssetProject(sourceDirectory, []);
        var selected = new HashSet<Guid>();
        var linkedPacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var required = new Stack<Guid>(project.Manifest.Assets.Values.SelectMany(entry => entry.Dependencies));
        string sharedDirectory = Path.Combine(_directory, "shared");
        Directory.CreateDirectory(sharedDirectory);
        while (required.TryPop(out Guid id))
        {
            if (project.Manifest.Assets.ContainsKey(id) || !selected.Add(id)) continue;
            AssetEntry entry = source.Manifest.Assets[id];
            foreach (Guid dependency in entry.Dependencies) required.Push(dependency);
            string linked = Path.Combine(sharedDirectory, $"{id:N}-{Path.GetFileName(entry.Path)}");
            string pack = "";
            if (entry.PackPath.Length == 0) File.CreateSymbolicLink(linked, Path.GetFullPath(entry.Path, sourceDirectory));
            else
            {
                string originalPack = Path.GetFullPath(entry.PackPath, sourceDirectory);
                if (!linkedPacks.TryGetValue(originalPack, out string? selectedPack))
                {
                    selectedPack = Path.Combine(sharedDirectory, $"pack-{linkedPacks.Count}.assetpack");
                    File.CreateSymbolicLink(selectedPack, originalPack);
                    linkedPacks.Add(originalPack, selectedPack);
                }
                pack = Path.GetRelativePath(_directory, selectedPack);
            }
            project.RegisterAsset(entry with { Path = linked, PackPath = pack, DefinitionPath = "", SourceGuid = default, SubAssetKey = "" });
        }
    }

    private async Task CreateShadersAsync(string engine)
    {
        string shaders = Path.Combine(_directory, "shaders");
        Directory.CreateDirectory(Path.Combine(shaders, "generated"));
        string sample = Path.Combine(engine, "samples", "DefaultGame", "assets", "Shaders");
        foreach (string file in new[] { "unlit.slang", "unlit_transparency.slang" })
            File.Copy(Path.Combine(sample, file), Path.Combine(shaders, file));
        RenderInstancePropertyLayout layout = RenderInstancePropertyLayout.Compose(ClusterRenderFeature.InstanceLayout, SkinInstanceProducer.Layout);
        int skinBinding = layout.Resolve<RenderSkinBinding>(SkinInstanceProducer.BindingKey).Descriptor.MetadataWordOffset;
        await File.WriteAllTextAsync(Path.Combine(shaders, "generated", "render_instance_layout.slang"),
            ClusterShaderSources.BuildInstanceLayout(layout).Replace("#endif",
                $"static const uint SKIN_BINDING_METADATA_WORD = {skinBinding}u;\n#endif", StringComparison.Ordinal));
        using var asset = new ShaderAsset { Name = "Saved transparency Unlit" };
        ShaderSourceBlock inputs = asset.GetBlock<ShaderSourceBlock>();
        inputs.SourceNames = ["cluster_transparency.slang", "unlit.slang", "unlit_transparency.slang"];
        inputs.Specializations =
        [
            new() { EntryPoint = "ClusterShadeMode", Arguments = ["0"] },
            new() { EntryPoint = "ClusterShadowRequests", Arguments = ["0", "0"] },
            new() { EntryPoint = "ClusterForwardShade", Arguments = ["0", "0"] },
            new() { EntryPoint = "BuildTransparentGeometry", Arguments = ["0", "false"] },
            new() { EntryPoint = "RequestTransparentPages", Arguments = ["0", "false"] },
            new() { EntryPoint = "ResolveTransparentColor", Arguments = ["0", "false"] },
            new() { EntryPoint = "ResolveProjectedTransparentColor", Arguments = ["0", "false"] },
            new() { EntryPoint = "ResolveUnorderedTransparentColor", Arguments = ["0", "false"] },
            new() { EntryPoint = "ResolveProjectedUnorderedTransparentColor", Arguments = ["0", "false"] },
        ];
        string path = Path.Combine(shaders, "panels.shader.asset");
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path,
            SlangShaderImporter.CreateDefaultTargets(slang), [Path.Combine(engine, "assets", "Shaders")]);
        await asset.CompileAsync(slang, settings);
        await asset.SaveAsync(path);
        await using ShaderAsset saved = await ShaderAsset.OpenAsync(path);
        Unordered = await saved.CreateSavedAsync(slang, settings);
        using ShaderSourceBlock completeInputs = saved.GetBlock<ShaderSourceBlock>().Duplicate();
        completeInputs.Specializations = completeInputs.Specializations
            .Where(value => value.EntryPoint is not ("ResolveUnorderedTransparentColor" or "ResolveProjectedUnorderedTransparentColor")).ToList();
        using ShaderBlock code = saved.GetBlock<ShaderBlock>().Duplicate();
        code.InputVersions = completeInputs.SourceNames.Select((_, index) =>
            new ShaderInputVersion(new(ShaderSourceBlock.BlockTypeId, index), completeInputs.DataVersion)).ToList();
        saved.SetBlock(completeInputs);
        saved.SetBlock(code);
        Complete = await saved.CreateSavedAsync(slang, settings);
    }

    private static Mesh CreatePanel()
    {
        Vector3[] positions = [new(-2, -2, 0), new(2, -2, 0), new(-2, 2, 0), new(2, 2, 0)];
        StandardClusterAttributes[] attributes = positions.Select(position => new StandardClusterAttributes(
            -Vector3.UnitZ, new(1, 0, 0, 1), new((position.X + 2) / 4, (position.Y + 2) / 4))).ToArray();
        return ClusterMeshBuilder.Build(positions, attributes, [0, 2, 1, 1, 2, 3], [new(0, 6, 0)],
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Saved transparency panel");
    }

    private async Task<Shader> CreateCurvedShaderAsync()
    {
        string shaders = Path.Combine(_directory, "shaders");
        foreach (string file in new[] { "deformed-phong.shader", "deformed-phong.slang", "deformed-phong_transparency.slang" })
            File.Copy(Path.Combine(_engine, "samples", "DefaultGame", "assets", "Shaders", file), Path.Combine(shaders, file));
        string source = Path.Combine(shaders, "deformed-phong.shader");
        using ShaderAsset shader = await ShaderAsset.OpenSourceAsync(source);
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(source,
            SlangShaderImporter.CreateDefaultTargets(slang), [Path.Combine(_engine, "assets", "Shaders")]);
        await shader.CompileAsync(slang, settings);
        string path = source + ".asset";
        await shader.SaveAsync(path);
        await using ShaderAsset saved = await ShaderAsset.OpenAsync(path);
        return await saved.CreateSavedAsync(slang, settings);
    }

    private static Mesh CreateCurvedMesh()
    {
        var (positions, indices, _) = PrimitiveMeshGenerator.CreateIcoSphere(0);
        StandardClusterAttributes[] attributes = positions.Select(position => new StandardClusterAttributes(
            Vector3.Normalize(position), new(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, position)), 1),
            new(MathF.Atan2(position.Z, position.X) / (2 * MathF.PI) + .5f, MathF.Asin(Vector3.Normalize(position).Y) / MathF.PI + .5f))).ToArray();
        return ClusterMeshBuilder.Build(positions, attributes, indices, [new(0, indices.Length, 0)],
            StandardClusterVertexEncoder.Instance, ClusterBuildStrategy.Realtime, "Saved Wave Phong sphere");
    }

    private static Texture CreateTexture(byte[] pixels, uint size) => Texture.Create(
        new(TextureDimension.Texture2D, size, size, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = size, Height = size, RowPitch = size * 4, SlicePitch = size * size * 4 }, pixels)],
        TextureViewDimension.Texture2D);

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
