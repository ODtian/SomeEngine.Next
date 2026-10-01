using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderDefinitionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "someengine-shader-definition-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    public ShaderDefinitionTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "Directory.Build.props"), "<Project />");
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task IndependentDefinitionUsesDeclaredEntriesWithoutCompanionMetadata()
    {
        string definitionPath = Path.Combine(_project, "Surface.shader");
        File.WriteAllText(Path.Combine(_project, "Geometry.slang"), """
            [shader("vertex")] float4 Vertex(uint id : SV_VertexID) : SV_Position { return float4(0, 0, 0, 1); }
            [shader("pixel")] float4 Unselected() : SV_Target { return float4(1, 0, 0, 1); }
            """);
        File.WriteAllText(Path.Combine(_project, "Lighting.slang"), """
            [shader("pixel")] float4 Pixel() : SV_Target { return float4(0, 1, 0, 1); }
            """);
        // A same-named source has no role in the definition.
        File.WriteAllText(Path.Combine(_project, "Surface.slang"), "This is intentionally invalid Slang.");
        using var definition = new ShaderAsset { Name = "surface" };
        ShaderSourceBlock fields = definition.GetBlock<ShaderSourceBlock>();
        fields.SourceNames = ["Geometry.slang", "Lighting.slang"];
        await definition.SaveSourceAsync(definitionPath);
        string text = File.ReadAllText(definitionPath);
        Assert.DoesNotContain("ImportTrace", text);
        Assert.DoesNotContain("Programs", text);
        var project = new AssetProject(_project, [], sourceFormats: [new ShaderSourceFormat()]);
        Guid id = await project.BuildAsync(definitionPath);
        AssetEntry output = Assert.Single(project.List());
        output = output with { Path = Path.Combine(_project, output.Path) };
        Assert.Equal(definition.Id, id);
        Assert.Equal(id, project.Resolve(definitionPath));
        var reloaded = new AssetProject(_project, [], sourceFormats: [new ShaderSourceFormat()]);
        Assert.Equal(id, reloaded.Resolve(definitionPath));
        Assert.Equal(definition.Id, output.AssetGuid);
        using ShaderAsset built = await ShaderAsset.OpenAsync(output.Path);
        Assert.Equal(
            definition.GetBlock<ShaderSourceBlock>().SourceNames,
            built.GetBlock<ShaderSourceBlock>().SourceNames);
        Shader shader = await ShaderTestCompilation.CreateAsync(built);
        Assert.Equal(2, shader.SlangTargetCount);
        for (int targetIndex = 0; targetIndex < shader.SlangTargetCount; targetIndex++)
        {
            Assert.Equal(
                ["Vertex", "Unselected", "Pixel"],
                Enumerable.Range(0, shader.SlangEntryCount)
                    .Select(index => shader.GetSlangReflection(targetIndex)
                        .GetEntryPointByIndex(checked((uint)index)).Name)
                    .ToArray());
        }
        Assert.Empty(Directory.GetFiles(_project, "*.meta", SearchOption.AllDirectories));
        var trace = built.GetBlock<ShaderBlock>().ImportTrace;
        Assert.NotNull(trace);
        Assert.Contains(trace.Dependencies!, item => item.Path == "Surface.shader");
        Assert.DoesNotContain(trace.Dependencies!, item => item.Path == "Surface.slang");
        string duplicate = Path.Combine(_project, "Duplicate.shader");
        File.Copy(definitionPath, duplicate);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await project.BuildAsync(duplicate));
        Assert.False(File.Exists(duplicate + ".asset"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await project.BuildAsync(definitionPath, cancellationToken: canceled.Token));
        Assert.Equal(id, Assert.Single(project.List()).AssetGuid);
    }

    [Fact]
    public async Task ExternalIncludesAreTrackedAndFailedBuildPreservesThePreviousOutput()
    {
        string library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        string include = Path.Combine(library, "color.inc");
        File.WriteAllText(include, "float4 color() { return float4(1, 0, 0, 1); }");
        string source = Path.Combine(_project, "Main.slang");
        File.WriteAllText(source, "#include \"color.inc\"\n[shader(\"pixel\")] float4 Pixel() : SV_Target { return color(); }");
        string path = Path.Combine(_project, "Material.shader");
        using var definition = new ShaderAsset();
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["Main.slang"];
        await definition.SaveSourceAsync(path);
        AssetEntry first = await SlangShaderImporter.BuildAsync(path, includeDirectories: [library]);
        string? firstHash;
        using (ShaderAsset built = await ShaderAsset.OpenAsync(first.Path))
        {
            var trace = built.GetBlock<ShaderBlock>().ImportTrace;
            Assert.NotNull(trace);
            firstHash = trace.ContentFingerprint;
            Assert.Contains(trace.Dependencies!, item => item.Path == "../library/color.inc");
        }
        File.WriteAllText(include, "float4 color() { return float4(0, 1, 0, 1); }");
        AssetEntry changed = await SlangShaderImporter.BuildAsync(path, includeDirectories: [library]);
        Assert.Equal(first.AssetGuid, changed.AssetGuid);
        Assert.NotEqual(first.Version, changed.Version);
        using (ShaderAsset built = await ShaderAsset.OpenAsync(changed.Path))
            Assert.NotEqual(firstHash, built.GetBlock<ShaderBlock>().ImportTrace!.ContentFingerprint);
        byte[] successful = File.ReadAllBytes(changed.Path);
        File.WriteAllText(source, "This is not Slang.");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await SlangShaderImporter.BuildAsync(path, includeDirectories: [library]));
        Assert.Contains("Main.slang", failure.Message);
        Assert.Equal(successful, File.ReadAllBytes(changed.Path));
        Assert.Empty(Directory.GetFiles(_project, "*.tmp"));
    }

    [Fact]
    public async Task ProjectBuildReusesOnlyVerifiedOutputsAndRebuildsChangedInputs()
    {
        string library = Path.Combine(_root, "library");
        Directory.CreateDirectory(library);
        string include = Path.Combine(library, "color.inc");
        File.WriteAllText(include, "float4 color() { return float4(1, 0, 0, 1); }");
        string source = Path.Combine(_project, "Draw.slang");
        File.WriteAllText(source, "#include \"color.inc\"\n[shader(\"pixel\")] float4 Pixel() : SV_Target { return color(); }");
        string path = Path.Combine(_project, "Draw.shader");
        using var definition = new ShaderAsset { Name = "draw" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["Draw.slang"];
        await definition.SaveSourceAsync(path);
        var project = new AssetProject(_project, [], sourceFormats: [new ShaderSourceFormat([library])]);
        Guid id = await project.BuildAsync(path);
        string output = Path.Combine(_project, Assert.Single(project.List()).Path);
        string record = SourceMeta.GetBuildPath(path);
        Guid version = Assert.Single(project.List()).Version;
        byte[] bytes = File.ReadAllBytes(output);
        byte[] metadata = File.ReadAllBytes(record);
        Assert.Equal(id, await project.BuildAsync(path));
        Assert.Equal(version, Assert.Single(project.List()).Version);
        Assert.Equal(bytes, File.ReadAllBytes(output));
        Assert.Equal(metadata, File.ReadAllBytes(record));
        File.WriteAllText(Path.Combine(_project, "Unused.slang"), "unrelated invalid source");
        await project.BuildAsync(path);
        Assert.Equal(version, Assert.Single(project.List()).Version);

        File.WriteAllText(include, "float4 color() { return float4(0, 1, 0, 1); }");
        await Rebuild();
        definition.Name = "renamed";
        await definition.SaveSourceAsync(path);
        await Rebuild();
        Assert.Equal("renamed", Assert.Single(project.List()).Name);
        using (ShaderAsset built = await ShaderAsset.OpenAsync(output))
        {
            Shader shader = await ShaderTestCompilation.CreateAsync(built);
            Assert.Equal(2, shader.SlangTargetCount);
        }
        File.WriteAllBytes(output, [1, 2, 3]);
        await Restore();
        File.Delete(record);
        await Restore();
        File.WriteAllText(record, "incomplete derived record");
        await Restore();
        File.WriteAllText(record, "{\"outputs\":null}");
        await Restore();
        File.Delete(output);
        await Restore();

        bytes = File.ReadAllBytes(output);
        metadata = File.ReadAllBytes(record);
        File.WriteAllText(source, "invalid Slang");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await project.BuildAsync(path));
        Assert.Equal(bytes, File.ReadAllBytes(output));
        Assert.Equal(metadata, File.ReadAllBytes(record));
        Assert.Equal(version, Assert.Single(project.List()).Version);
        Assert.Empty(Directory.EnumerateFiles(_project, "*.meta"));
        Assert.Empty(Directory.EnumerateFiles(_project, ".*.tmp.*"));

        await project.DeleteAssetAsync(id);
        Assert.False(File.Exists(record));
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(path));

        async Task Rebuild()
        {
            Assert.Equal(id, await project.BuildAsync(path));
            Guid next = Assert.Single(project.List()).Version;
            Assert.NotEqual(version, next);
            version = next;
        }
        async Task Restore()
        {
            Assert.Equal(id, await project.BuildAsync(path));
            Assert.Equal(version, Assert.Single(project.List()).Version);
            using ShaderAsset built = await ShaderAsset.OpenAsync(output);
            Assert.Equal(id, built.Id);
            Shader shader = await ShaderTestCompilation.CreateAsync(built);
            Assert.Equal(2, shader.SlangTargetCount);
            using var savedRecord = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(record));
            Assert.Equal(version, savedRecord.RootElement.GetProperty("outputs")[0].GetProperty("version").GetGuid());
        }
    }

    [Fact]
    public void IncludeLibrariesAreExplicitAndNotFoundThroughAncestorProjectMarkers()
    {
        File.WriteAllText(Path.Combine(_root, "SomeEngine.slnx"), "");
        string library = Path.Combine(_root, "assets", "Shaders");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "selected.inc"), "float4 color() { return 1; }");
        File.WriteAllText(Path.Combine(_project, "Draw.slang"),
            "#include \"selected.inc\"\n[shader(\"pixel\")] float4 Pixel() : SV_Target { return color(); }");
        using var definition = new ShaderAsset();
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["Draw.slang"];
        string path = Path.Combine(_project, "surface.shader");
        Assert.Throws<InvalidDataException>(() => SlangShaderImporter.CompileDefinition(definition, path));
        using var result = SlangShaderImporter.CompileDefinition(definition, path, includeDirectories: [library], projectRoot: _project);
        Assert.Contains(
            ShaderSourceBlock.SerializedSourceKey(0),
            result.GetBlock<ShaderSourceBlock>().ContentKeys);
        Assert.Contains(result.GetBlock<ShaderBlock>().ImportTrace!.Dependencies!,
            input => input.Path == "../assets/Shaders/selected.inc");
    }
}
