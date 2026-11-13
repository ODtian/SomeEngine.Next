using System.Buffers;
using SlangShaderSharp;
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
    public async Task ShaderLibrarySharesSavedShadersAcrossContentDirectories()
    {
        string firstDirectory = Path.Combine(_project, "first");
        string secondDirectory = Path.Combine(_project, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        string source = Path.Combine(firstDirectory, "shared.slang");
        await File.WriteAllTextAsync(source,
            "RWStructuredBuffer<uint> Result; [shader(\"compute\")] [numthreads(1,1,1)] void Main() { Result[0] = 7; }");
        using var asset = new ShaderAsset { Name = "shared" };
        asset.GetBlock<ShaderSourceBlock>().SourceNames = ["shared.slang"];
        var settings = SlangShaderImporter.CreateSessionDescription(source,
            SlangShaderImporter.CreateTargets(ShaderTestCompilation.Slang, [SlangShaderSharp.SlangCompileTarget.Spirv]));
        await asset.CompileAsync(ShaderTestCompilation.Slang, settings);
        AssetEntry entry = await asset.SaveAsync(Path.Combine(firstDirectory, "shared.shader.asset"));
        string secondPath = Path.Combine(secondDirectory, "shared.shader.asset");
        File.Copy(entry.Path, secondPath);
        var first = new AssetProject(firstDirectory, []);
        var second = new AssetProject(secondDirectory, []);
        first.RegisterAsset(entry);
        second.RegisterAsset(entry with { Path = secondPath });
        await using var firstFiles = new AssetLoader(first.CreateStorage());
        await using var secondFiles = new AssetLoader(second.CreateStorage());
        using var library = new ShaderLibrary(SlangShaderSharp.SlangCompileTarget.Spirv);

        Shader original = await library.GetAsync(firstFiles, asset.Reference);
        Shader shared = await library.GetAsync(secondFiles, asset.Reference);
        Assert.Same(original, shared);
        Assert.True(shared.GetSlangEntryBytes(0, 0).Length > 0);
    }

    [Fact]
    public async Task ChangedTransitiveInputKeepsShaderCodeAndNamedResourcesTogether()
    {
        string valuePath = Path.Combine(_project, "values.shader");
        string valueSource = Path.Combine(_project, "values.slang");
        string sharedPath = Path.Combine(_project, "shared.shader");
        string sharedSource = Path.Combine(_project, "shared.slang");
        string entryPath = Path.Combine(_project, "entry.shader");
        string entrySource = Path.Combine(_project, "entry.slang");
        await File.WriteAllTextAsync(valueSource, """
            RWStructuredBuffer<uint> SharedResult;
            uint ReadValue() { SharedResult[0] = 3; return 3; }
            """);
        await File.WriteAllTextAsync(sharedSource, """
            import values;
            uint SharedValue() { return ReadValue(); }
            """);
        await File.WriteAllTextAsync(entrySource, """
            import shared;
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)] void Main() { Result[0] = SharedValue(); }
            [shader("compute")] [numthreads(1,1,1)] void Second() { Result[1] = SharedValue() + 1; }
            """);
        using var value = new ShaderAsset { Name = "values" };
        value.GetBlock<ShaderSourceBlock>().SourceNames = ["values.slang"];
        value.GetBlock<ShaderSourceBlock>().SourceOnly = true;
        await value.SaveSourceAsync(valuePath);
        using var shared = new ShaderAsset { Name = "shared" };
        shared.GetBlock<ShaderSourceBlock>().SourceNames = ["shared.slang"];
        shared.GetBlock<ShaderSourceBlock>().SourceOnly = true;
        await shared.SaveSourceAsync(sharedPath);
        using var entry = new ShaderAsset { Name = "entry" };
        entry.GetBlock<ShaderSourceBlock>().SourceNames = ["entry.slang"];
        await entry.SaveSourceAsync(entryPath);
        var project = new AssetProject(_project, [], shaderTargets: [SlangShaderSharp.SlangCompileTarget.Spirv]);
        await project.BuildAllAsync([entryPath], 1);
        await using var firstFiles = new AssetLoader(project.CreateStorage());
        using var shaders = new ShaderLibrary(SlangShaderSharp.SlangCompileTarget.Spirv);
        Shader original = await shaders.GetAsync(firstFiles, entry.Reference);
        byte[] originalCode = ReadEntryCode(original, "Main");
        Assert.Same(original, await shaders.GetAsync(firstFiles, entry.Reference));
        using (ShaderAsset originalInput = await ShaderAsset.OpenAsync(entryPath + ".asset"))
        {
            Shader expectedOriginal = (await shaders.CompileAsync(originalInput, firstFiles)).Shader;
            foreach (string name in new[] { "Main", "Second" })
                Assert.Equal(ReadEntryCode(expectedOriginal, name), ReadEntryCode(original, name));
        }

        await File.WriteAllTextAsync(valueSource, """
            RWStructuredBuffer<uint> AddedResult;
            RWStructuredBuffer<uint> SharedResult;
            uint ReadValue() { SharedResult[0] = 7; return 7; }
            """);
        await project.BuildAllAsync([valuePath], 1);
        File.Delete(valueSource);
        File.Delete(sharedSource);
        File.Delete(entrySource);
        await using var currentFiles = new AssetLoader(project.CreateStorage());
        Shader current = await shaders.GetAsync(currentFiles, entry.Reference);
        using ShaderAsset reference = await ShaderAsset.OpenAsync(entryPath + ".asset");
        Shader expected = (await shaders.CompileAsync(reference, currentFiles)).Shader;

        Assert.NotSame(original, current);
        Assert.Same(current, await shaders.GetAsync(currentFiles, entry.Reference));
        Assert.Equal(originalCode, ReadEntryCode(original, "Main"));
        Assert.NotEqual(originalCode, ReadEntryCode(current, "Main"));
        foreach (string name in new[] { "Main", "Second" })
        {
            Assert.Equal(ReadEntryCode(expected, name), ReadEntryCode(current, name));
            string[] used = SomeEngine.Render.ShaderParameterResources.Create(current,
                    SlangShaderSharp.SlangCompileTarget.Spirv, name).Resources.ToArray()
                .Where(resource => resource.IsUsed).Select(resource => resource.Name).Order().ToArray();
            Assert.Equal(["Result", "SharedResult"], used);
        }

        static byte[] ReadEntryCode(Shader shader, string name)
        {
            var entryPoint = shader.RequireSlangEntry(0, name, SlangShaderSharp.SlangStage.Compute);
            return shader.GetSlangEntryBlob(0, entryPoint) is { } blob
                ? blob.Buffer.ToArray() : shader.GetSlangEntryBytes(0, entryPoint).ToArray();
        }
    }

    [Fact]
    public async Task AssetLoaderOpensAssetsFromTheirContentDirectories()
    {
        var application = new AssetProject(Path.Combine(_project, "application"), []);
        var engine = new AssetProject(Path.Combine(_project, "engine"), []);
        Directory.CreateDirectory(application.RootPath);
        Directory.CreateDirectory(engine.RootPath);
        using var first = new ShaderAsset { Name = "application" };
        using var second = new ShaderAsset { Name = "engine" };
        application.RegisterAsset(await first.SaveAsync(Path.Combine(application.RootPath, "shared.shader.asset")));
        engine.RegisterAsset(await second.SaveAsync(Path.Combine(engine.RootPath, "shared.shader.asset")));

        await using var loader = new AssetLoader([application.CreateStorage(), engine.CreateStorage()]);
        using ShaderAsset applicationShader = await ShaderAsset.OpenAsync(loader, first.Reference);
        using ShaderAsset engineShader = await ShaderAsset.OpenAsync(loader, second.Reference);
        Assert.Equal(first.Id, applicationShader.Id);
        Assert.Equal(first.Name, applicationShader.Name);
        Assert.Equal(second.Id, engineShader.Id);
        Assert.Equal(second.Name, engineShader.Name);
    }

    [Fact]
    public async Task BuildAllRegistersShadersForTheSelectedTarget()
    {
        string[] paths = [Path.Combine(_project, "first.shader"), Path.Combine(_project, "second.shader")];
        foreach (string path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            await File.WriteAllTextAsync(Path.Combine(_project, name + ".slang"),
                "[shader(\"compute\")] [numthreads(1,1,1)] void Main() {}");
            using var definition = new ShaderAsset { Name = name };
            definition.GetBlock<ShaderSourceBlock>().SourceNames = [name + ".slang"];
            await definition.SaveSourceAsync(path);
        }
        var project = new AssetProject(_project, [], shaderTargets: [SlangShaderSharp.SlangCompileTarget.Spirv]);
        await project.BuildAllAsync(paths, 2);

        var reopened = new AssetProject(_project, [], shaderTargets: project.ShaderTargets);
        Assert.Equal(2, reopened.List().Count);
        foreach (string path in paths)
        {
            Assert.Equal(project.Resolve(path), reopened.Resolve(path));
            using ShaderAsset asset = await ShaderAsset.OpenAsync(path + ".asset");
            var settings = SlangShaderImporter.CreateSessionDescription(path,
                SlangShaderImporter.CreateTargets(ShaderTestCompilation.Slang, project.ShaderTargets));
            Shader shader = await SomeEngine.Resources.ShaderLibrary.CreateAsync(asset, ShaderTestCompilation.Slang, settings);
            Assert.Equal(1, shader.SlangTargetCount);
            Assert.Equal(SlangShaderSharp.SlangCompileTarget.Spirv, shader.GetSlangTarget(0).Format);
            Assert.True(shader.GetSlangEntryBytes(0, 0).Length > 0);
        }
    }

    [Fact]
    public async Task SharedInputsBuildAsAssetReferencesAndRefreshTheirConsumers()
    {
        string inputPath = Path.Combine(_project, "shared.shader");
        string entryPath = Path.Combine(_project, "entry.shader");
        string sourcePath = Path.Combine(_project, "shared.slang");
        await File.WriteAllTextAsync(sourcePath, """
            uint SharedValue() { return 3; }
            struct SharedParameters { uint Value; };
            RWStructuredBuffer<uint> SharedResult;
            [shader("compute")] [numthreads(1,1,1)] void SharedMain() { SharedResult[0] = SharedValue(); }
            """);
        await File.WriteAllTextAsync(Path.Combine(_project, "entry.slang"), """
            import shared;
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void Main() { Result[0] = SharedValue(); }
            """);
        using var input = new ShaderAsset { Name = "shared" };
        input.GetBlock<ShaderSourceBlock>().SourceNames = ["shared.slang"];
        await input.SaveSourceAsync(inputPath);
        using var entry = new ShaderAsset { Name = "entry" };
        entry.GetBlock<ShaderSourceBlock>().SourceNames = ["entry.slang"];
        await entry.SaveSourceAsync(entryPath);
        var project = new AssetProject(_project, [], shaderTargets: [SlangShaderSharp.SlangCompileTarget.Spirv]);
        await project.BuildAllAsync([entryPath], 2);

        using (ShaderAsset built = await ShaderAsset.OpenAsync(entryPath + ".asset"))
            Assert.Equal(input.Reference, Assert.Single(built.GetBlock<ShaderSourceBlock>().Dependencies));
        using (var record = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(SourceMeta.GetBuildPath(entryPath))))
        {
            Assert.Equal(input.Id, Assert.Single(record.RootElement.GetProperty("asset_inputs").EnumerateArray()).GetGuid());
            Assert.DoesNotContain(record.RootElement.GetProperty("inputs").EnumerateArray(),
                value => value.GetProperty("relative_path").GetString() == "shared.slang");
        }
        byte[] first;
        await using (var files = new AssetLoader(project.CreateStorage()))
        using (var shaders = new ShaderLibrary(SlangShaderSharp.SlangCompileTarget.Spirv))
            first = (await shaders.GetAsync(files, entry.Reference)).GetSlangEntryBytes(0, 0).ToArray();

        await File.WriteAllTextAsync(sourcePath, """
            uint SharedValue() { return 7; }
            struct SharedParameters { uint Value; };
            RWStructuredBuffer<uint> SharedResult;
            [shader("compute")] [numthreads(1,1,1)] void SharedMain() { SharedResult[0] = SharedValue(); }
            """);
        await project.BuildAllAsync([entryPath], 2);
        File.Delete(sourcePath);
        File.Delete(Path.Combine(_project, "entry.slang"));
        await using var restoredFiles = new AssetLoader(project.CreateStorage());
        var settings = SlangShaderImporter.CreateSessionDescription(entryPath,
            SlangShaderImporter.CreateTargets(ShaderTestCompilation.Slang, project.ShaderTargets));
        settings.FileSystem = new EmptyShaderFileSystem();
        using var restoredShaders = new ShaderLibrary(ShaderTestCompilation.Slang, settings);
        Shader restored = await restoredShaders.GetAsync(restoredFiles, entry.Reference);
        Assert.NotEqual(first, restored.GetSlangEntryBytes(0, 0).ToArray());
        Assert.Contains(restored.GetSlangDeclarations(), declaration =>
            Enumerable.Range(0, declaration.ChildrenCount).Any(index =>
                declaration.GetChild((uint)index).Kind == SlangShaderSharp.DeclReflectionKind.Struct &&
                declaration.GetChild((uint)index).Type.Name == "SharedParameters"));
        Assert.Same(restored, await restoredShaders.GetAsync(restoredFiles, entry.Reference));
        await restoredShaders.RefreshAsync();
        Assert.Same(restored, await restoredShaders.GetAsync(restoredFiles, entry.Reference));
    }

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
        var project = new AssetProject(_project, []);
        Guid id = await project.BuildAsync(definitionPath);
        AssetEntry output = Assert.Single(project.List());
        output = output with { Path = Path.Combine(_project, output.Path) };
        Assert.Equal(definition.Id, id);
        Assert.Equal(id, project.Resolve(definitionPath));
        var reloaded = new AssetProject(_project, []);
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
        AssetEntry first = await SlangShaderImporter.BuildAsync(path, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv], includeDirectories: [library]);
        string? firstHash;
        using (ShaderAsset built = await ShaderAsset.OpenAsync(first.Path))
        {
            var trace = built.GetBlock<ShaderBlock>().ImportTrace;
            Assert.NotNull(trace);
            firstHash = trace.ContentFingerprint;
            Assert.Contains(trace.Dependencies!, item => item.Path == "../library/color.inc");
        }
        File.WriteAllText(include, "float4 color() { return float4(0, 1, 0, 1); }");
        AssetEntry changed = await SlangShaderImporter.BuildAsync(path, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv], includeDirectories: [library]);
        Assert.Equal(first.AssetGuid, changed.AssetGuid);
        Assert.NotEqual(first.Version, changed.Version);
        using (ShaderAsset built = await ShaderAsset.OpenAsync(changed.Path))
            Assert.NotEqual(firstHash, built.GetBlock<ShaderBlock>().ImportTrace!.ContentFingerprint);
        byte[] successful = File.ReadAllBytes(changed.Path);
        File.WriteAllText(source, "This is not Slang.");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(async () => await SlangShaderImporter.BuildAsync(path, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv], includeDirectories: [library]));

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
        var project = new AssetProject(_project, [], shaderIncludeDirectories: [library]);
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
    public async Task IncludeLibrariesAreExplicitAndNotFoundThroughAncestorProjectMarkers()
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
        Assert.Throws<InvalidDataException>(() => SlangShaderImporter.CompileDefinition(definition, path, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv]));
        using var result = SlangShaderImporter.CompileDefinition(definition, path, [SlangShaderSharp.SlangCompileTarget.Dxil, SlangShaderSharp.SlangCompileTarget.Spirv], includeDirectories: [library], projectRoot: _project);
        Assert.False((await result.GetBlock<ShaderSourceBlock>().ReadBytesAsync(ShaderSourceBlock.SerializedSourceKey(0))).IsEmpty);
        Assert.Contains(result.GetBlock<ShaderBlock>().ImportTrace!.Dependencies!,
            input => input.Path == "../assets/Shaders/selected.inc");
    }
}
