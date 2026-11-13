using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialEntryImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-MaterialEntries-" + Guid.NewGuid().ToString("N"));

    public MaterialEntryImportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task MaterialOnlyDefinitionsRestoreOpaqueForwardAndLayerDebugEntriesFromSavedInputs()
    {
        string sourcePath = Path.Combine(_directory, "constant_material.slang");
        string definitionPath = Path.Combine(_directory, "constant_material.shader");
        await File.WriteAllTextAsync(sourcePath, MaterialSource);
        using var definition = new ShaderAsset { Name = "Constant material" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["constant_material.slang"];
        await definition.SaveSourceAsync(definitionPath);
        string shaders = ShaderDirectory();
        using ShaderAsset built = SlangShaderImporter.CompileDefinition(definition, definitionPath,
            [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv], includeDirectories: [shaders], projectRoot: _directory);
        ShaderSourceBlock source = built.GetBlock<ShaderSourceBlock>();
        Assert.Equal(new[] { "constant_material.slang", "constant_material.entries.slang" }, source.SourceNames);
        Assert.Equal(27, source.Specializations.Count);
        Assert.Contains(source.Dependencies, input => input.Value == EngineShaderContent.GetShaderId(ProjectRoot(), "surface_inputs.shader"));
        Assert.Contains(source.Dependencies, input => input.Value == EngineShaderContent.GetShaderId(ProjectRoot(), "shade_surface_entries.shader"));
        Assert.Contains(source.Dependencies, input => input.Value == EngineShaderContent.GetShaderId(ProjectRoot(), "surface_outputs.shader"));
        string savedPath = Path.Combine(_directory, "constant_material.shader.asset");
        await built.SaveAsync(savedPath);
        File.Delete(sourcePath);
        File.Delete(definitionPath);

        using ShaderAsset restored = await ShaderAsset.OpenAsync(savedPath);
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(definitionPath,
            SlangShaderImporter.CreateTargets(slang, [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv]), [shaders]);
        using var library = new ShaderLibrary(slang, settings);
        Shader shader = await library.CreateAsync(restored);

        foreach (string entryName in new[] { "ClusterShadeMode", "LayerShadeMode" })
            foreach (string mode in new[] { "0", "8", "9", "10", "11" })
            {
                Shader variant = shader.RequireSpecialization(entryName, [mode]);
                Assert.False(variant.HasSpecializations(entryName));
                for (int target = 0; target < 2; target++)
                {
                    EntryPointReflection entry = variant.RequireSlangEntry(target, entryName, SlangStage.Compute);
                    Assert.True(variant.GetSlangEntryBytes(target, entry).Length > 0);
                }
            }
        foreach (string depth in new[] { "0", "1", "2" })
            foreach (string mode in new[] { "0", "8", "9", "10", "11" })
            {
                Shader variant = shader.RequireSpecialization("ClusterForwardShade", [depth, mode]);
                Assert.True(UsesResource(variant, "ClusterForwardShade", "OutputColor"));
                for (int target = 0; target < 2; target++)
                    Assert.True(variant.GetSlangEntryBytes(target,
                        variant.RequireSlangEntry(target, "ClusterForwardShade", SlangStage.Compute)).Length > 0);
            }
        foreach (string requests in new[] { "LayerNoPageRequests", "LayerShadowPageRequests<32>" })
        {
            Shader variant = shader.RequireSpecialization("LayerRasterSoftware", [requests]);
            for (int target = 0; target < 2; target++)
                Assert.True(variant.GetSlangEntryBytes(target,
                    variant.RequireSlangEntry(target, "LayerRasterSoftware", SlangStage.Compute)).Length > 0);
        }
        Shader opaque = shader.RequireSpecialization("ClusterShadeMode", ["0"]);
        FunctionReflection function = opaque.RequireSlangEntry(0, "ClusterShadeMode", SlangStage.Compute).Function;
        Assert.Contains(Enumerable.Range(0, (int)function.AttributeCount).Select(index => function.GetAttribute((uint)index)),
            attribute => attribute.Name == "VertexEncoding" && attribute.GetArgumentValueString(0) ==
                "someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1");
        Assert.Empty(Directory.GetFiles(_directory, "*.entries.slang"));
    }

    [Fact]
    public async Task BuildAllBuildsTheGeneratedEntryLibrariesBeforeTheMaterial()
    {
        string shaders = ShaderDirectory();
        string librarySource = Path.Combine(_directory, "shade_surface_entries.slang");
        await File.WriteAllTextAsync(librarySource,
            "#include \"" + Path.Combine(shaders, "shade_surface_entries.slang").Replace('\\', '/') + "\"\n");
        using var entries = new ShaderAsset { Name = "Local shared material entries" };
        entries.GetBlock<ShaderSourceBlock>().SourceNames = ["shade_surface_entries.slang"];
        entries.GetBlock<ShaderSourceBlock>().SourceOnly = true;
        string entriesPath = Path.Combine(_directory, "shade_surface_entries.shader");
        await entries.SaveSourceAsync(entriesPath);
        await File.WriteAllTextAsync(Path.Combine(_directory, "constant_material.slang"), MaterialSource);
        using var definition = new ShaderAsset { Name = "Constant material" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["constant_material.slang"];
        string definitionPath = Path.Combine(_directory, "constant_material.shader");
        await definition.SaveSourceAsync(definitionPath);
        var project = new AssetProject(_directory, [], shaderIncludeDirectories: [shaders],
            shaderTargets: [SlangCompileTarget.Spirv]);

        await project.BuildAllAsync([definitionPath], 2);

        Assert.Equal(entries.Id, project.Resolve(entriesPath));
        using ShaderAsset built = await ShaderAsset.OpenAsync(definitionPath + ".asset");
        Assert.Contains(entries.Reference, built.GetBlock<ShaderSourceBlock>().Dependencies);
        Assert.Equal(27, built.GetBlock<ShaderSourceBlock>().Specializations.Count);
    }

    [Fact]
    public async Task MarkedDomainsBakeTessellatedRasterAndDomainReconstruction()
    {
        string definitionPath = Path.Combine(_directory, "curved_material.shader");
        string material = MaterialSource.Replace("import triangle_surface;", "import triangle_surface;\nimport phong_tessellation;", StringComparison.Ordinal)
            .Replace("typedef LinearSurface Domain;", "typedef PhongTessellation Domain;", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(_directory, "curved_material.slang"), material);
        using var definition = new ShaderAsset { Name = "Curved material" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["curved_material.slang"];
        string shaders = ShaderDirectory();
        using ShaderAsset built = SlangShaderImporter.CompileDefinition(definition, definitionPath,
            [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv], includeDirectories: [shaders], projectRoot: _directory);
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(definitionPath,
            SlangShaderImporter.CreateTargets(slang, [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv]), [shaders]);
        using var library = new ShaderLibrary(slang, settings);

        Shader shader = await library.CreateAsync(built);

        foreach (string entryName in new[] { "ClusterShadeMode", "LayerShadeMode" })
        {
            Shader variant = shader.RequireSpecialization(entryName, ["0"]);
            Assert.False(UsesResource(variant, entryName, "LayerTessellation"));
            for (int target = 0; target < 2; target++)
                Assert.True(variant.GetSlangEntryBytes(target,
                    variant.RequireSlangEntry(target, entryName, SlangStage.Compute)).Length > 0);
        }
        Shader raster = shader.RequireSpecialization("LayerRasterSoftware", ["LayerNoPageRequests"]);
        Assert.True(UsesResource(raster, "LayerRasterSoftware", "LayerTessellation"));
        for (int target = 0; target < 2; target++)
            Assert.True(raster.GetSlangEntryBytes(target,
                raster.RequireSlangEntry(target, "LayerRasterSoftware", SlangStage.Compute)).Length > 0);
        (string Name, SlangStage Stage)[] tessellation =
        [
            ("ClusterTessellationSeed",SlangStage.Compute),
            ("ClusterTessellationSplit",SlangStage.Compute),
            ("ClusterTessellationDiceImageAtomic64",SlangStage.Compute),
            ("ClusterTessellationDiceBufferAtomic64",SlangStage.Compute),
            ("ClusterTessellationDiceNonAtomic",SlangStage.Compute),
            ("ClusterTessellationHardwareVertex",SlangStage.Vertex),
            ("ClusterTessellationHardwareMesh",SlangStage.Mesh),
            ("ClusterTessellationHardwarePixel",SlangStage.Fragment),
            ("ClusterTessellationHardwareMeshPixel",SlangStage.Fragment),
            ("LayerRasterVertex",SlangStage.Vertex),
            ("LayerRasterPixel",SlangStage.Fragment),
            ("LayerRasterPixelNoRequests",SlangStage.Fragment),
        ];
        foreach (var expected in tessellation)
            for (int target = 0; target < 2; target++)
                Assert.True(shader.GetSlangEntryBytes(target,
                    shader.RequireSlangEntry(target, expected.Name, expected.Stage)).Length > 0);
    }

    [Fact]
    public async Task MaterialDefinitionsExposeTheScreenAndShadowRasterEntryFamily()
    {
        string definitionPath = Path.Combine(_directory, "raster_material.shader");
        string source = MaterialSource.Replace("[ConservativeBounds(\"finite\")]",
            "[ConservativeBounds(\"unbounded\")]\n[RasterFaces(\"both\",\"front\")]", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(_directory, "raster_material.slang"), source);
        using var definition = new ShaderAsset { Name = "Screen and shadow material" };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["raster_material.slang"];
        string shaders = ShaderDirectory();
        using ShaderAsset built = SlangShaderImporter.CompileDefinition(definition, definitionPath,
            [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv], includeDirectories: [shaders], projectRoot: _directory);
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(definitionPath,
            SlangShaderImporter.CreateTargets(slang, [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv]), [shaders]);
        using var library = new ShaderLibrary(slang, settings);

        Shader shader = await library.CreateAsync(built);

        (string Name, SlangStage Stage)[] entries =
        [
            ("ClusterRasterSoftwareImageAtomic64",SlangStage.Compute),
            ("ClusterRasterSoftwareBufferAtomic64",SlangStage.Compute),
            ("ClusterRasterSoftwareNonAtomic",SlangStage.Compute),
            ("ClusterRasterSoftwareTarget",SlangStage.Compute),
            ("ClusterRasterVertex",SlangStage.Vertex),
            ("ClusterRasterMesh",SlangStage.Mesh),
            ("ClusterRasterPixelImageAtomic64",SlangStage.Fragment),
            ("ClusterRasterPixelBufferAtomic64",SlangStage.Fragment),
            ("ClusterRasterPixelNonAtomic",SlangStage.Fragment),
            ("ClusterRasterPixelHardwareOnly",SlangStage.Fragment),
            ("ClusterRasterMeshPixelImageAtomic64",SlangStage.Fragment),
            ("ClusterRasterMeshPixelBufferAtomic64",SlangStage.Fragment),
            ("ClusterRasterMeshPixelNonAtomic",SlangStage.Fragment),
            ("ClusterRasterMeshPixelHardwareOnly",SlangStage.Fragment),
            ("ClusterRasterPixelTarget",SlangStage.Fragment),
            ("ClusterRasterMeshPixelTarget",SlangStage.Fragment),
            ("ClusterShade",SlangStage.Compute),
            ("LayerRasterVertex",SlangStage.Vertex),
            ("LayerRasterPixel",SlangStage.Fragment),
            ("LayerRasterPixelNoRequests",SlangStage.Fragment),
        ];
        foreach (var expected in entries)
            for (int target = 0; target < 2; target++)
            {
                EntryPointReflection entry = shader.RequireSlangEntry(target, expected.Name, expected.Stage);
                Assert.True(shader.GetSlangEntryBytes(target, entry).Length > 0);
                FunctionReflection function = entry.Function;
                if (expected.Stage == SlangStage.Fragment)
                {
                    AttributeReflection faces = Enumerable.Range(0, (int)function.AttributeCount)
                        .Select(index => function.GetAttribute((uint)index)).Single(attribute => attribute.Name == "RasterFaces");
                    Assert.Equal("both", faces.GetArgumentValueString(0));
                    Assert.Equal("front", faces.GetArgumentValueString(1));
                }
                else if (expected.Name != "ClusterShade")
                {
                    AttributeReflection bounds = Enumerable.Range(0, (int)function.AttributeCount)
                        .Select(index => function.GetAttribute((uint)index)).Single(attribute => attribute.Name == "ConservativeBounds");
                    Assert.Equal("unbounded", bounds.GetArgumentValueString(0));
                }
            }
        TypeReflection visibility = shader.GetSlangReflection(0).FindTypeByName("VisibilityEvaluate")!.Value;
        Assert.Contains(Enumerable.Range(0, (int)visibility.AttributeCount).Select(index => visibility.GetAttribute((uint)index)),
            attribute => attribute.Name == "VisibilityDepth" && attribute.GetArgumentValueString(0) == "opaque");
        Assert.Contains(built.GetBlock<ShaderBlock>().ImportTrace!.Dependencies!,
            dependency => Path.GetFileName(dependency.Path) == "cluster_material_entries.slang");
    }

    private static bool UsesResource(Shader shader, string entry, string name)
    {
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(shader,
                     shader.GetSlangTarget(0).Format, entry).Resources)
            if (resource.Name == name && resource.IsUsed) return true;
        return false;
    }

    private static string ProjectRoot()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent!;
        return directory.FullName;
    }

    private static string ShaderDirectory() => Path.Combine(ProjectRoot(), "assets", "Shaders");

    private const string MaterialSource = """
        import material_interfaces;
        import vertex_evaluate;
        import triangle_surface;
        [VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
        [ConservativeBounds("finite")]
        struct ConstantImportMaterial : IMaterial
        {
            typedef StaticVertexEval Vertex;
            typedef LinearSurface Domain;
            typedef OpaqueVisibilityEvaluate<Vertex.Vertex> Visibility;
            void evaluateSurface<TOutput:ISurfaceOutput>(SurfacePixelInput input,Domain.Surface surface,
                float3 bary,Domain.TriangleData triangle,TOutput output)
            { output.Write(input.PixelCoord,float4(0.25,0.5,0.75,0.5)); }
            void evaluateDebug<TOutput:ISurfaceOutput>(uint mode,SurfacePixelInput input,Domain.Surface surface,
                float3 bary,Domain.TriangleData triangle,TOutput output)
            { output.Write(input.PixelCoord,float4(float(mode)/16.0,bary.x,bary.y,1.0)); }
        };
        """;
}
