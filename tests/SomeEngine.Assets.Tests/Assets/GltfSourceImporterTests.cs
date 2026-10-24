using SomeEngine.Geometry.Cluster;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class GltfSourceImporterTests
{
    [Fact]
    public async Task RepeatedMeshNodesShareOneAssetAndKeepTheirParentInTheWorld()
    {
        string directory = CreateTempDir();
        try
        {
            string source = WriteTestProject(directory, writeImporterSettings: false);
            string json = File.ReadAllText(source)
                .Replace("{ \"mesh\": 0 },", "{ \"mesh\": 0, \"children\": [2] },", StringComparison.Ordinal)
                .Replace("{ \"mesh\": 1 }", "{ \"mesh\": 1 }, { \"mesh\": 0 }", StringComparison.Ordinal);
            File.WriteAllText(source, json);
            IReadOnlyList<ImportedAsset> imported = await ImportAsync(SomeEngine.Assets.Importers.StandardGltfSceneImporter.CreateSourceImporter(), directory, source);
            Assert.Equal(2, imported.Count(value => value.SubAssetKey.StartsWith("mesh:")));
            ImportedAsset scene = Assert.Single(imported, value => value.SubAssetKey.StartsWith("scene:"));
            using WorldAsset asset = await WorldAsset.OpenAsync(scene.OutputPath);
            Assert.Equal(2, asset.ExternalReferences.Length);
            using World world = await asset.LoadAsync(SceneSerialization.CreateRegistry());
            var entities = new List<Entity>();
            QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<MeshSource>());
            try
            {
                world.ExecuteQuery(query, ref entities, static (scoped QueryCursor rows, ref List<Entity> values) =>
                {
                    foreach (var row in rows.Rows) values.Add(row.Entity);
                });
            }
            finally { world.ReleaseQuery(query); }
            Assert.Equal(3, entities.Count);
            Guid sharedMesh = Assert.Single(imported, value => value.SubAssetKey == "mesh:index:0").AssetGuid;
            Entity[] matching = entities.Where(entity => world.Read<MeshSource>(entity).Mesh.Value == sharedMesh).ToArray();
            Assert.Equal(2, matching.Length);
            Assert.Single(matching, entity => matching.Contains(Hierarchy.GetParent(world, entity)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ImportCanBeRepeatedWithoutCallerMemoryStorage()
    {
        string directory = CreateTempDir();
        try
        {
            string path = WriteTestProject(directory, writeImporterSettings: false);
            var importer = SomeEngine.Assets.Importers.StandardGltfSceneImporter.CreateSourceImporter();
            var assets = await ImportAsync(importer, directory, path);
            Assert.Contains(assets, value => value.SubAssetKey.StartsWith("texture:"));
            await ImportAsync(importer, directory, path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task GeometryImportPreservesUnboundRegionsAndSourceCorrespondence()
    {
        string dir = CreateTempDir();
        try
        {
            string path = WriteTestProject(dir, writeImporterSettings: false);
            var model = SharpGLTF.Schema2.ModelRoot.Load(path);
            var source = model.LogicalMeshes[0];
            source.Primitives[0].Material = null;
            await using var context = new ImportContext();
            var geometry = new GltfMeshImporter();
            var settings = new GltfMeshSettings();
            context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(new AssetImporter<SharpGLTF.Schema2.Mesh, GltfMeshSettings>(
                new Guid("ce474674-bd5e-4ae0-b72e-2d50d64e6413"), [ClusterGeometryBlock.BlockTypeId], new ClusterGeometryImporter(), settings));
            context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(new AssetImporter<SharpGLTF.Schema2.Mesh, GltfMeshSettings>(
                new Guid("535b2c57-2964-483b-867a-f38ef1a9fe5b"), [ClusterVertexAttributesBlock.BlockTypeId], new ClusterVertexAttributesImporter(), settings));
            context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(new AssetImporter<SharpGLTF.Schema2.Mesh, GltfMeshSettings>(
                Guid.NewGuid(), [VertexStreamsBlock.BlockTypeId], new GltfVertexStreamsImporter(), settings));
            context.EnableImporter<SharpGLTF.Schema2.Mesh, MeshAsset>(new AssetImporter<SharpGLTF.Schema2.Mesh, GltfMeshSettings>(
                new Guid("334ba003-337f-4298-ae72-1a5ce4861591"), [MeshBlock.BlockTypeId], geometry, settings));
            string key = $"mesh/{source.LogicalIndex}";
            Guid id = context.GetAssetId(key);
            context.AddAsset(source, new MeshAsset(id), key, id + ".mesh.asset");
            MeshAsset mesh = await context.GetAssetAsync<SharpGLTF.Schema2.Mesh, MeshAsset>(source);
            Assert.Single(mesh.GetBlock<MeshBlock>().Regions);
            Assert.True(mesh.ContainsBlock<ClusterGeometryBlock>());
            Assert.False(mesh.ContainsBlock<MeshMaterialsBlock>());
            MeshRegionSource origin = Assert.Single(mesh.GetBlock<MeshBlock>().RegionSources);
            Assert.Equal(source.LogicalIndex, origin.MeshIndex);
            Assert.Equal(0, origin.PrimitiveIndex);
            Assert.Equal(-1, origin.MaterialIndex);
            string output = Path.Combine(dir, "unbound.mesh.asset");
            await mesh.SaveAsync(output);
            using MeshAsset reopened = await MeshAsset.OpenAsync(output);
            Assert.Equal(origin, Assert.Single(reopened.GetBlock<MeshBlock>().RegionSources));
            Assert.Empty(reopened.Describe().Dependencies);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
    private const string LitShaderGuid = "3ecf4b6f-4ca4-4ad0-88e2-37688ad9b010";
    private const string UnlitShaderGuid = "4774aa8d-36c4-40db-9821-d982e19f9f84";

    [Fact]
    public async Task TexturelessGltfMaterialsUseSharedEngineTemplateImages()
    {
        string directory = CreateTempDir();
        try
        {
            string source = WriteTestProject(directory, writeImporterSettings: false);
            JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(source))!;
            foreach (JsonNode? material in document["materials"]!.AsArray())
            {
                var values = material!.AsObject();
                var surface = values["pbrMetallicRoughness"]!.AsObject();
                surface.Remove("baseColorTexture");
                surface.Remove("metallicRoughnessTexture");
                values.Remove("normalTexture");
                values.Remove("occlusionTexture");
            }
            await File.WriteAllTextAsync(source, document.ToJsonString());
            AssetProject project = AssetProjects.Create(directory);
            await MaterialTemplates.SaveAsync(project, default, default);
            await project.ImportAsync(source);
            await using var loader = new AssetLoader(project.CreateStorage());
            using MaterialAsset standardAsset = await MaterialAsset.OpenAsync(loader,
                new(project.List(MaterialAsset.FileType).Single(value => value.Name == "MaskedLit").AssetGuid));
            using MaterialAsset unlitAsset = await MaterialAsset.OpenAsync(loader,
                new(project.List(MaterialAsset.FileType).Single(value => value.Name == "TransparentUnlit").AssetGuid));

            Dictionary<AssetId<ShaderAsset>, Shader> shaders = [];
            Dictionary<AssetId<TextureAsset>, Texture> textures = [];
            using MaterialBlock content = await standardAsset.ResolveAsync(loader);
            foreach (AssetId<TextureAsset> id in content.Parameters
                .Where(value => value.Kind == MaterialParameterKind.Texture)
                .Select(value => value.Texture).Distinct())
            {
                using TextureAsset texture = await TextureAsset.OpenAsync(loader, id);
                textures.Add(id, await texture.GetBlock<TextureBlock>().CreateAsync(texture.Name));
            }
            StandardMaterial standard = await standardAsset.CreateAsync<StandardMaterial>(loader, shaders, textures);
            UnlitMaterial unlit = await unlitAsset.CreateAsync<UnlitMaterial>(loader, shaders, textures);

            Assert.Equal(MaterialTemplates.Standard, await standardAsset.GetTemplateAsync(loader));
            Assert.Equal(MaterialTemplates.Unlit, await unlitAsset.GetTemplateAsync(loader));
            Assert.Same(standard.AlbedoMap, standard.ARMMap);
            Assert.Same(standard.AlbedoMap, unlit.AlbedoMap);
            Assert.True(standard.NormalMap!.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var normal));
            Assert.Equal(new byte[] { 128, 128, 255, 255 }, normal.ToArray());
            Assert.Equal(.35f, standard.Roughness);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ImportUsesEngineMaterialsAndProjectShaders()
    {
        string dir = CreateTempDir();

        try
        {
            string gltfPath = WriteTestProject(dir, writeImporterSettings: false);
            AssetProject project = AssetProjects.Create(dir);
            await project.ImportAsync(gltfPath);
            AssetEntry entry = project.List(MaterialAsset.FileType).Single(asset => asset.Name == "MaskedLit");
            string path = Path.Combine(dir, entry.Path);
            await using MaterialAsset material = await MaterialAsset.OpenAsync(path);
            Assert.IsType<MaterialAsset>(material);
            Assert.Equal(MaterialTemplates.Standard, material.GetBlock<MaterialBlock>().Parent);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ImportingUnchangedSourceReusesAssetVersions()
    {
        string dir = CreateTempDir();
        try
        {
            string source = WriteTestProject(dir, writeImporterSettings: false);
            AssetProject project = AssetProjects.Create(dir);
            await project.ImportAsync(source);
            var versions = project.List().OrderBy(asset => asset.AssetGuid).Select(asset => (asset.AssetGuid, asset.Version)).ToArray();

            await project.ImportAsync(source);

            Assert.Equal(versions, project.List().OrderBy(asset => asset.AssetGuid).Select(asset => (asset.AssetGuid, asset.Version)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ImportProducesUniqueAssetsWithPersistentKeysAndSharedMaterialDependencies()
    {
        string dir = CreateTempDir();

        try
        {
            string gltfPath = WriteTestProject(dir, writeImporterSettings: true);
            var importer = SomeEngine.Assets.Importers.StandardGltfSceneImporter.CreateSourceImporter();

            IReadOnlyList<ImportedAsset> first = await ImportAsync(importer, dir, gltfPath);
            IReadOnlyList<ImportedAsset> second = await ImportAsync(importer, dir, gltfPath);

            Assert.Equal(8, first.Count); // 3 textures, 2 materials, 2 meshes, 1 scene
            Assert.Equal(first.Count, first.Select(asset => asset.AssetGuid).Distinct().Count());
            Assert.Equal(first.Count, first.Select(asset => asset.SubAssetKey).Distinct().Count());
            Assert.Equal(
                first.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.SubAssetKey),
                second.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.SubAssetKey));
            Assert.Equal(
                first.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.AssetGuid),
                second.OrderBy(static asset => asset.SubAssetKey, StringComparer.Ordinal).Select(static asset => asset.AssetGuid));

            ImportedAsset maskedMaterial = Assert.Single(first, asset => asset.SubAssetKey == "material:index:0");
            ImportedAsset transparentMaterial = Assert.Single(first, asset => asset.SubAssetKey == "material:index:1");
            ImportedAsset bodyMesh = Assert.Single(first, asset => asset.SubAssetKey == "mesh:index:0");
            ImportedAsset eyesMesh = Assert.Single(first, asset => asset.SubAssetKey == "mesh:index:1");
            ImportedAsset scene = Assert.Single(first, asset => asset.SubAssetKey == "scene:index:0");
            Assert.Equal(new[] { bodyMesh.AssetGuid, eyesMesh.AssetGuid }.Order(), scene.Dependencies.Order());
            using WorldAsset worldAsset = await WorldAsset.OpenAsync(scene.OutputPath);
            Assert.True(worldAsset.ContentLength > 0);

            using StandardMaterialAsset maskedAsset = await StandardMaterialAsset.OpenAsync(maskedMaterial.OutputPath);
            using UnlitMaterialAsset transparentAsset = await UnlitMaterialAsset.OpenAsync(transparentMaterial.OutputPath);
            var maskedParameters = new StandardMaterialParameters(await maskedAsset.GetBlockAsync<MaterialBlock>());
            var transparentParameters = new UnlitMaterialParameters(await transparentAsset.GetBlockAsync<MaterialBlock>());
            using MeshAsset bodyAsset = await MeshAsset.OpenAsync(bodyMesh.OutputPath);
            using MeshAsset eyesAsset = await MeshAsset.OpenAsync(eyesMesh.OutputPath);
            SourceMeta sourceMeta = SourceMeta.Load(gltfPath);
            Assert.Equal(new[]
            {
                "assets/Materials/Templates/PbrTemplate.material.asset",
                "assets/Materials/Templates/UnlitTemplate.material.asset",
            }, sourceMeta.Build.ReportedInputs.Select(value => value.RelativePath.Replace('\\', '/')).Order(StringComparer.Ordinal));
            SourceOutput? bodyMeshMeta = sourceMeta.FindOutput(bodyMesh.OutputPath);
            Assert.Equal("MaskedLit", maskedAsset.Name);
            Assert.Equal("TransparentUnlit", transparentAsset.Name);
            Assert.Equal("Body", bodyAsset.Name);
            Assert.Equal("Eyes", eyesAsset.Name);

            Assert.Equal(
                new System.Numerics.Vector4(0.5f, 0.25f, 1.0f, 1.0f),
                maskedParameters.BaseColorTint);
            Assert.Equal(0.75f, maskedParameters.MetallicFactor);
            Assert.Equal(0.35f, maskedParameters.Roughness);
            Assert.Equal(
                new System.Numerics.Vector3(0.2f, 0.4f, 0.6f),
                maskedParameters.EmissiveFactor);
            Assert.False((maskedParameters.AlbedoMap.Value == Guid.Empty));
            Assert.False((maskedParameters.NormalMap.Value == Guid.Empty));
            Assert.False((maskedParameters.ARMMap.Value == Guid.Empty));

            Assert.Equal(
                new System.Numerics.Vector4(1.0f, 1.0f, 1.0f, 0.5f),
                transparentParameters.BaseColorTint);
            Assert.False((transparentParameters.AlbedoMap.Value == Guid.Empty));
            Assert.Equal(maskedParameters.AlbedoMap, transparentParameters.AlbedoMap);

            Assert.NotNull(bodyAsset.GetBlock<MeshBlock>().Regions);
            Assert.Single(bodyAsset.GetBlock<MeshBlock>().Regions);
            Assert.Equal("region_0", bodyAsset.GetBlock<MeshBlock>().Regions[0].Name);
            Assert.Equal([new AssetId<MaterialAsset>(maskedMaterial.AssetGuid)], bodyAsset.GetBlock<MeshMaterialsBlock>().Materials);
            Assert.Equal([maskedMaterial.AssetGuid], bodyMesh.Dependencies);
            Assert.NotNull(bodyMeshMeta);
            Assert.Equal(GltfSourceImporter.ImporterVersion, sourceMeta.ImporterVersion);

            Assert.NotNull(eyesAsset.GetBlock<MeshBlock>().Regions);
            Assert.Single(eyesAsset.GetBlock<MeshBlock>().Regions);
            Assert.Equal("region_0", eyesAsset.GetBlock<MeshBlock>().Regions[0].Name);
            Assert.Equal([new AssetId<MaterialAsset>(transparentMaterial.AssetGuid)], eyesAsset.GetBlock<MeshMaterialsBlock>().Materials);
            Assert.Equal([transparentMaterial.AssetGuid], eyesMesh.Dependencies);

            // Both materials share the same texture asset; dependencies do not produce it twice.
            var textureKeys = first.Where(asset => asset.SubAssetKey.StartsWith("texture:")).ToArray();
            Assert.Equal(3, textureKeys.Length);
            Assert.All(
                new[]
                {
                    maskedParameters.AlbedoMap.Value,
                    maskedParameters.NormalMap.Value,
                    maskedParameters.ARMMap.Value,
                },
                static guid => Assert.NotEqual(Guid.Empty, guid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputsTrackExternalBuffersAndImagesInTextAndBinaryGltf(bool binaryContainer)
    {
        string dir = CreateTempDir();

        try
        {
            string gltfPath = WriteTestProject(dir, writeImporterSettings: true);
            if (binaryContainer)
            {
                string binaryPath = Path.ChangeExtension(gltfPath, ".glb");
                WriteJsonGlb(gltfPath, binaryPath);
                SourceMeta.Save(binaryPath, SourceMeta.Load(gltfPath));
                gltfPath = binaryPath;
            }
            Assert.Equal(2, SharpGLTF.Schema2.ModelRoot.Load(gltfPath).LogicalMeshes.Count);
            SourceMeta sourceMeta = SourceMeta.Load(gltfPath);
            var importer = SomeEngine.Assets.Importers.StandardGltfSceneImporter.CreateSourceImporter();

            ImportInputs? first = await importer.GetInputsAsync(dir, gltfPath, sourceMeta, null, default);
            Assert.NotNull(first);

            string bufferPath = Path.Combine(dir, "assets", "Models", "character.bin");
            byte[] bufferBytes = File.ReadAllBytes(bufferPath);
            bufferBytes[0] ^= 0x1;
            File.WriteAllBytes(bufferPath, bufferBytes);

            ImportInputs? afterBufferChange = await importer.GetInputsAsync(dir, gltfPath, sourceMeta, null, default);
            Assert.NotNull(afterBufferChange);

            string imagePath = Path.Combine(dir, "assets", "Models", "textures", "albedo.png");
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            imageBytes[^1] ^= 0x1;
            File.WriteAllBytes(imagePath, imageBytes);

            ImportInputs? afterImageChange = await importer.GetInputsAsync(dir, gltfPath, sourceMeta, null, default);
            Assert.NotNull(afterImageChange);

            Assert.NotEqual(first.InputHash, afterBufferChange.InputHash);
            Assert.NotEqual(afterBufferChange.InputHash, afterImageChange.InputHash);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void WriteJsonGlb(string source, string destination)
    {
        byte[] json = Encoding.UTF8.GetBytes(File.ReadAllText(source));
        int length = (json.Length + 3) & ~3;
        byte[] container = new byte[20 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(container, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(8), checked((uint)container.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(12), checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(16), 0x4E4F534A);
        container.AsSpan(20).Fill(0x20);
        json.CopyTo(container.AsSpan(20));
        File.WriteAllBytes(destination, container);
    }

    [Fact]
    public async Task RenamingAnOrdinalElementPreservesItsAssetIdAndReferences()
    {
        string dir = CreateTempDir();
        try
        {
            string path = WriteTestProject(dir, writeImporterSettings: false);
            var importer = SomeEngine.Assets.Importers.StandardGltfSceneImporter.CreateSourceImporter();
            IReadOnlyList<ImportedAsset> original = await ImportAsync(importer, dir, path);
            ImportedAsset first = Assert.Single(original, asset => asset.SubAssetKey == "mesh:index:0");
            string source = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, source.Replace("\"Body\"", "\"RenamedBody\"", StringComparison.Ordinal));
            IReadOnlyList<ImportedAsset> changed = await ImportAsync(importer, dir, path);
            ImportedAsset renamed = Assert.Single(changed, asset => asset.SubAssetKey == "mesh:index:0");
            Assert.Equal(first.AssetGuid, renamed.AssetGuid);
            Assert.Equal(first.Dependencies, renamed.Dependencies);
            using MeshAsset mesh = await MeshAsset.OpenAsync(renamed.OutputPath);
            Assert.Equal("RenamedBody", mesh.Name);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<IReadOnlyList<ImportedAsset>> ImportAsync(GltfSourceImporter importer, string directory, string source)
    {
        var project = new AssetProject(directory, [importer]);
        IReadOnlyList<Guid> ids = await project.ImportAsync(source);
        return ids.Select(id =>
        {
            AssetEntry entry = project.Manifest.Assets[id];
            return ImportedAsset.Create(entry, entry.SubAssetKey, Path.Combine(directory, entry.Path));
        }).ToArray();
    }

    private static string WriteTestProject(string dir, bool writeImporterSettings)
    {
        MaterialTemplates.SaveAsync(new AssetProject(dir, []), new(Guid.Parse(LitShaderGuid)), new(Guid.Parse(UnlitShaderGuid)))
            .AsTask().GetAwaiter().GetResult();
        string templateDir = Path.Combine(dir, "assets", "Materials", "Templates");
        Directory.CreateDirectory(templateDir);
        WriteTemplate(Path.Combine(templateDir, "PbrTemplate.material.asset"), "LitTemplate", unlit: false);
        WriteTemplate(Path.Combine(templateDir, "UnlitTemplate.material.asset"), "UnlitTemplate", unlit: true);
        string shaderDirectory = Path.Combine(dir, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        using (var lit = new ShaderAsset(Guid.Parse(LitShaderGuid)))
            lit.SaveSourceAsync(Path.Combine(shaderDirectory, "standard.shader")).AsTask().GetAwaiter().GetResult();
        using (var unlit = new ShaderAsset(Guid.Parse(UnlitShaderGuid)))
            unlit.SaveSourceAsync(Path.Combine(shaderDirectory, "unlit.shader")).AsTask().GetAwaiter().GetResult();

        string modelDir = Path.Combine(dir, "assets", "Models");
        string textureDir = Path.Combine(modelDir, "textures");
        Directory.CreateDirectory(textureDir);
        WriteTinyPng(Path.Combine(textureDir, "albedo.png"));
        WriteTinyPng(Path.Combine(textureDir, "metallic_roughness.png"));
        WriteTinyPng(Path.Combine(textureDir, "normal.png"));
        WriteTinyPng(Path.Combine(textureDir, "occlusion.png"));
        WriteTinyPng(Path.Combine(textureDir, "emissive.png"));

        string gltfPath = Path.Combine(modelDir, "character.gltf");
        string binPath = Path.Combine(modelDir, "character.bin");
        File.WriteAllBytes(binPath, BuildTriangleBuffer());
        File.WriteAllText(gltfPath, BuildGltfJson(), new UTF8Encoding(false));

        if (writeImporterSettings)
        {
            JsonElement settings = JsonDocument.Parse(
                """
                {
                  "lit_material_template": "assets/Materials/Templates/PbrTemplate.material.asset",
                  "unlit_material_template": "assets/Materials/Templates/UnlitTemplate.material.asset",
                  "require_persistent_element_ids": false
                }
                """).RootElement.Clone();

            SourceMeta.Save(gltfPath, new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importers = [new(SomeEngine.Assets.Importers.GltfSourceImporter.SourceImporterId, settings)],
            });
        }
        else SourceMeta.Save(gltfPath, new SourceMeta
        {
            SourceGuid = SourceGuid.New(),
            Importers = [new(GltfSourceImporter.SourceImporterId,
                JsonSerializer.SerializeToElement(new GltfImporterSettings { RequirePersistentElementIds = false },
                    GltfSettingsJson.Default.GltfImporterSettings))],
        });

        return gltfPath;
    }

    private static void WriteTemplate(string path, string name, bool unlit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using MaterialAsset material = unlit
            ? new UnlitMaterialAsset(Guid.NewGuid()) {
                Name = name,
            }
            : new StandardMaterialAsset(Guid.NewGuid()) {
                Name = name,
            };
        material.GetBlock<MaterialBlock>().Shader = new(Guid.Parse(unlit ? UnlitShaderGuid : LitShaderGuid));
        ((material)).SaveAsync(path).AsTask().GetAwaiter().GetResult();
    }

private static byte[] BuildTriangleBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WritePositions(writer, new[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f,
        });
        WriteIndices(writer, new ushort[] { 0, 1, 2 });
        writer.Write((ushort)0); // pad to 4-byte alignment

        WritePositions(writer, new[]
        {
            0f, 0f, 1f,
            1f, 0f, 1f,
            0f, 1f, 1f,
        });
        WriteIndices(writer, new ushort[] { 0, 1, 2 });

        writer.Flush();
        return stream.ToArray();
    }

    private static void WritePositions(BinaryWriter writer, float[] values)
    {
        foreach (float value in values)
        {
            writer.Write(value);
        }
    }

    private static void WriteIndices(BinaryWriter writer, ushort[] values)
    {
        foreach (ushort value in values)
        {
            writer.Write(value);
        }
    }

    private static string BuildGltfJson()
    {
        return
            """
            {
              "asset": { "version": "2.0" },
              "scene": 0,
              "scenes": [
                { "nodes": [0, 1] }
              ],
              "nodes": [
                { "mesh": 0 },
                { "mesh": 1 }
              ],
              "buffers": [
                { "uri": "character.bin", "byteLength": 86 }
              ],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
                { "buffer": 0, "byteOffset": 36, "byteLength": 6, "target": 34963 },
                { "buffer": 0, "byteOffset": 44, "byteLength": 36, "target": 34962 },
                { "buffer": 0, "byteOffset": 80, "byteLength": 6, "target": 34963 }
              ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" },
                { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 1], "max": [1, 1, 1] },
                { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ],
              "images": [
                { "uri": "textures/albedo.png" },
                { "uri": "textures/metallic_roughness.png" },
                { "uri": "textures/normal.png" },
                { "uri": "textures/occlusion.png" },
                { "uri": "textures/emissive.png" }
              ],
              "textures": [
                { "source": 0 },
                { "source": 1 },
                { "source": 2 },
                { "source": 3 },
                { "source": 4 }
              ],
              "materials": [
                {
                  "name": "MaskedLit",
                  "pbrMetallicRoughness": {
                    "baseColorFactor": [0.5, 0.25, 1.0, 1.0],
                    "baseColorTexture": { "index": 0 },
                    "metallicFactor": 0.75,
                    "roughnessFactor": 0.35,
                    "metallicRoughnessTexture": { "index": 1 }
                  },
                  "normalTexture": { "index": 2 },
                  "occlusionTexture": { "index": 3 },
                  "emissiveFactor": [0.2, 0.4, 0.6],
                  "emissiveTexture": { "index": 4 },
                  "alphaMode": "MASK",
                  "alphaCutoff": 0.33,
                  "doubleSided": true
                },
                {
                  "name": "TransparentUnlit",
                  "extensions": {
                    "KHR_materials_unlit": {}
                  },
                  "pbrMetallicRoughness": {
                    "baseColorFactor": [1.0, 1.0, 1.0, 0.5],
                    "baseColorTexture": { "index": 0 }
                  },
                  "alphaMode": "BLEND"
                }
              ],
              "meshes": [
                {
                  "name": "Body",
                  "primitives": [
                    {
                      "attributes": { "POSITION": 0 },
                      "indices": 1,
                      "material": 0
                    }
                  ]
                },
                {
                  "name": "Eyes",
                  "primitives": [
                    {
                      "attributes": { "POSITION": 2 },
                      "indices": 3,
                      "material": 1
                    }
                  ]
                }
              ],
              "extensionsUsed": [ "KHR_materials_unlit" ]
            }
            """;
    }

    private static void WriteTinyPng(string path)
    {
        const string tinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Z0ioAAAAASUVORK5CYII=";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Convert.FromBase64String(tinyPngBase64));
    }

}
