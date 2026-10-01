using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetManifestTests
{
    [Fact]
    public void SelfReferencesArePreservedAsDependencyFacts()
    {
        Guid id = Guid.NewGuid();
        var manifest = new AssetManifest();
        manifest.AddAsset(id, "self", "self.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash,
            dependencies: [id, Guid.Empty, id]);
        Assert.Equal(new[] { id }, manifest.GetDependencies(id));
    }

    [Fact]
    public void SaveLoad_Roundtrip_PreservesSourcesAssetsAndDependencies()
    {
        string dir = CreateTempDir();

        try
        {
            SourceGuid sourceGuid = SourceGuid.New();
            Guid shaderGuid = Guid.NewGuid();
            Guid materialGuid = Guid.NewGuid();

            AssetManifest manifest = new();
            manifest.AddSource(sourceGuid, "assets/Shaders/test.slang");
            manifest.AddAsset(shaderGuid, "TestShader", "assets/Shaders/test.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, sourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "TestMaterial", "assets/Materials/test.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [shaderGuid]);
            manifest.Save(dir);
            Assert.Equal(AssetManifest.CatalogFileName, Path.GetFileName(Assert.Single(Directory.EnumerateFiles(dir))));

            AssetManifest loaded = AssetManifest.Load(dir);

            Assert.True(loaded.TrySourcePath(sourceGuid, out string? sourcePath));
            Assert.Equal("assets/Shaders/test.slang", sourcePath);
            Assert.True(loaded.TryGetAsset(shaderGuid, out AssetEntry shaderRecord));
            Assert.Equal("TestShader", shaderRecord.Name);
            Assert.Equal(sourceGuid, shaderRecord.SourceGuid);
            Assert.Equal(ShaderAsset.SchemaHash, shaderRecord.SchemaFingerprint);
            Assert.Equal(new[] { shaderGuid }, loaded.GetDependencies(materialGuid));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AddAssetRejectsZeroSchemaFingerprint()
    {
        var manifest = new AssetManifest();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            manifest.AddAsset(
                Guid.NewGuid(),
                "Probe",
                "assets/probes/value.probe.asset",
                "ProbeAsset",
                schemaFingerprint: 0));
    }

    [Fact]
    public void RemoveSourceAndAssetInvalidateEveryDirectIndex()
    {
        SourceGuid sourceGuid = SourceGuid.New();
        Guid assetGuid = Guid.NewGuid();
        var manifest = new AssetManifest();
        manifest.AddSource(sourceGuid, "assets/Shaders/obsolete.slang");
        manifest.AddAsset(
            assetGuid,
            "Obsolete",
            "assets/Shaders/obsolete.shader.asset",
            ShaderAsset.FileType,
            ShaderAsset.SchemaHash,
            sourceGuid,
            "shader:main");

        Assert.True(manifest.RemoveAsset(assetGuid));
        Assert.True(manifest.RemoveSource(sourceGuid));
        Assert.False(manifest.RemoveAsset(assetGuid));
        Assert.False(manifest.RemoveSource(sourceGuid));
        Assert.False(manifest.TryGetAsset(assetGuid, out _));
        Assert.False(manifest.TryAssetPath("assets/Shaders/obsolete.shader.asset", out _));
        Assert.False(manifest.TrySourceGuid("assets/Shaders/obsolete.slang", out _));
        Assert.Empty(manifest.AssetsBySource(sourceGuid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000000000000000")]
    [InlineData("not-a-fingerprint")]
    public void LoadRejectsMissingOrInvalidSchemaFingerprint(string? schemaFingerprint)
    {
        string directory = CreateTempDir();

        try
        {
            Guid guid = Guid.NewGuid();
            var entry = new JsonObject
            {
                ["asset_guid"] = guid.ToString(),
                ["name"] = "Probe",
                ["path"] = "assets/probes/value.probe.asset",
                ["asset_type"] = "ProbeAsset",
                ["source_guid"] = string.Empty,
                ["sub_asset_key"] = string.Empty,
            };
            if (schemaFingerprint is not null)
                entry["schema_fingerprint"] = schemaFingerprint;

            var document = new JsonObject
            {
                ["assets"] = new JsonObject { ["assets"] = new JsonArray(entry) },
            };
            File.WriteAllText(
                Path.Combine(directory, AssetManifest.CatalogFileName),
                document.ToJsonString());

            Assert.Throws<InvalidDataException>(() => AssetManifest.Load(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Queries_ByPathSourceAndSubAssetKey_Work()
    {
        SourceGuid sourceGuid = SourceGuid.New();
        Guid mainGuid = Guid.NewGuid();
        Guid shadowGuid = Guid.NewGuid();

        AssetManifest manifest = new();
        manifest.AddSource(sourceGuid, "assets/Shaders/shared.slang");
        manifest.AddAsset(mainGuid, "Main", "assets/Shaders/shared.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, sourceGuid, "shader:main");
        manifest.AddAsset(shadowGuid, "Shadow", "assets/Shaders/shared.shadow.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, sourceGuid, "shader:shadow");

        Assert.True(manifest.TrySourceGuid("assets/Shaders/shared.slang", out SourceGuid resolvedSourceGuid));
        Assert.Equal(sourceGuid, resolvedSourceGuid);
        Assert.True(manifest.TryAssetPath("assets/Shaders/shared.shader.asset", out AssetEntry assetRecord));
        Assert.Equal(mainGuid, assetRecord.AssetGuid);
        Assert.True(manifest.TrySourceAsset(sourceGuid, "shader:shadow", out AssetEntry subAssetRecord));
        Assert.Equal(shadowGuid, subAssetRecord.AssetGuid);
        AssertEquivalent(manifest.AssetsBySource(sourceGuid), new[] { mainGuid, shadowGuid });
    }

    [Fact]
    public void GetReferencers_TracksIncomingDependencies()
    {
        Guid textureGuid = Guid.NewGuid();
        Guid materialGuid = Guid.NewGuid();
        Guid meshGuid = Guid.NewGuid();

        AssetManifest manifest = new();
        manifest.AddAsset(textureGuid, "TextureAsset", "assets/Textures/test.texture.asset", TextureAsset.FileType, TextureAsset.SchemaHash);
        manifest.AddAsset(materialGuid, "MaterialData", "assets/Materials/test.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [textureGuid]);
        manifest.AddAsset(meshGuid, "MeshAsset", "assets/Meshes/test.mesh.asset", MeshAsset.FileType, MeshAsset.SchemaHash, dependencies: [materialGuid]);

        Assert.Equal(new[] { materialGuid }, manifest.GetReferencers(textureGuid));
        Assert.Equal(new[] { meshGuid }, manifest.GetReferencers(materialGuid));
        Assert.Empty(manifest.GetReferencers(meshGuid));
    }

    [Fact]
    public void List_CanFilterByAssetType()
    {
        Guid shaderGuid = Guid.NewGuid();
        Guid materialGuid = Guid.NewGuid();

        AssetManifest manifest = new();
        manifest.AddAsset(shaderGuid, "ShaderAsset", "assets/Shaders/test.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash);
        manifest.AddAsset(materialGuid, "MaterialData", "assets/Materials/test.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash, dependencies: [shaderGuid]);

        IReadOnlyList<AssetEntry> materials = manifest.List(MaterialAsset.FileType);

        Assert.Single(materials);
        Assert.Equal(materialGuid, materials[0].AssetGuid);
        Assert.Equal("MaterialData", materials[0].Name);
    }

    [Fact]
    public void MissingCatalogDoesNotReadObsoleteIndexesAndCorruptionDoesNotFallBack()
    {
        string directory = CreateTempDir();
        try
        {
            foreach (string name in new[] { "source_index.json", "asset_index.json", "dependency_graph.json" })
                File.WriteAllText(Path.Combine(directory, name), "Obsolete data must not be read.");
            AssetManifest empty = AssetManifest.Load(directory);
            Assert.Empty(empty.Sources);
            Assert.Empty(empty.Assets);
            File.WriteAllText(Path.Combine(directory, AssetManifest.CatalogFileName), "null");
            Assert.Throws<InvalidDataException>(() => AssetManifest.Load(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentReadersObserveWholeCatalogsDuringRepeatedReplacement()
    {
        string directory = CreateTempDir();
        try
        {
            SourceGuid firstSource = SourceGuid.New(), secondSource = SourceGuid.New();
            Guid material = Guid.NewGuid(), firstShader = Guid.NewGuid(), secondShader = Guid.NewGuid();
            AssetManifest first = Create(firstSource, firstShader, "first");
            AssetManifest second = Create(secondSource, secondShader, "second");
            first.Save(directory);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task writer = Task.Run(async () =>
            {
                await entered.Task;
                for (int index = 0; index < 100; index++) (index % 2 == 0 ? second : first).Save(directory);
            });
            Task reader = Task.Run(() =>
            {
                entered.SetResult();
                int reads = 0;
                do
                {
                    AssetManifest loaded = AssetManifest.Load(directory);
                    SourceGuid source = Assert.Single(loaded.Sources).Key;
                    bool isFirst = source == firstSource;
                    Assert.True(isFirst || source == secondSource);
                    Guid shader = isFirst ? firstShader : secondShader;
                    Assert.Equal(2, loaded.Assets.Count);
                    Assert.True(loaded.TryGetAsset(material, out AssetEntry entry));
                    Assert.Equal(source, entry.SourceGuid);
                    Assert.Equal(isFirst ? "first" : "second", entry.Name);
                    Assert.Equal(shader, Assert.Single(loaded.GetDependencies(material)));
                    Assert.True(loaded.TryGetAsset(shader, out _));
                    reads++;
                } while (!writer.IsCompleted || reads < 100);
            });
            await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(30));

            AssetManifest Create(SourceGuid source, Guid shader, string name)
            {
                var value = new AssetManifest();
                value.AddSource(source, $"assets/{name}.gltf");
                value.AddAsset(shader, name, $"assets/{name}.shader.asset", ShaderAsset.FileType, ShaderAsset.SchemaHash, source, "shader");
                value.AddAsset(material, name, "assets/result.material.asset", MaterialAsset.FileType, MaterialAsset.SchemaHash,
                    source, "material", [shader]);
                return value;
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertEquivalent(IEnumerable<Guid> actual, IEnumerable<Guid> expected)
    {
        Assert.Equal(
            expected.OrderBy(static value => value.ToString(), StringComparer.Ordinal),
            actual.OrderBy(static value => value.ToString(), StringComparer.Ordinal));
    }
}
