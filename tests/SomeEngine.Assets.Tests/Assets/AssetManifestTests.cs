using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public class AssetManifestTests
{
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
            manifest.AddAsset(shaderGuid, "TestShader", "assets/Shaders/test.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash, sourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "TestMaterial", "assets/Materials/test.material.asset", Material.Asset.FileType, Material.Asset.SchemaHash, dependencies: [shaderGuid]);
            manifest.Save(dir);

            AssetManifest loaded = AssetManifest.Load(dir);

            Assert.True(loaded.TrySourcePath(sourceGuid, out string? sourcePath));
            Assert.Equal("assets/Shaders/test.slang", sourcePath);
            Assert.True(loaded.TryGetAsset(shaderGuid, out AssetEntry shaderRecord));
            Assert.Equal("TestShader", shaderRecord.Name);
            Assert.Equal(sourceGuid, shaderRecord.SourceGuid);
            Assert.Equal(Shader.Asset.SchemaHash, shaderRecord.SchemaFingerprint);
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
            Shader.Asset.FileType,
            Shader.Asset.SchemaHash,
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
                ["assets"] = new JsonArray(entry),
            };
            File.WriteAllText(
                Path.Combine(directory, AssetManifest.AssetIndexFileName),
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
        manifest.AddAsset(mainGuid, "Main", "assets/Shaders/shared.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash, sourceGuid, "shader:main");
        manifest.AddAsset(shadowGuid, "Shadow", "assets/Shaders/shared.shadow.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash, sourceGuid, "shader:shadow");

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
        manifest.AddAsset(textureGuid, "Texture.Asset", "assets/Textures/test.texture.asset", Texture.Asset.FileType, Texture.Asset.SchemaHash);
        manifest.AddAsset(materialGuid, "MaterialData", "assets/Materials/test.material.asset", Material.Asset.FileType, Material.Asset.SchemaHash, dependencies: [textureGuid]);
        manifest.AddAsset(meshGuid, "Mesh.Asset", "assets/Meshes/test.mesh.asset", Mesh.Asset.FileType, Mesh.Asset.SchemaHash, dependencies: [materialGuid]);

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
        manifest.AddAsset(shaderGuid, "Shader.Asset", "assets/Shaders/test.shader.asset", Shader.Asset.FileType, Shader.Asset.SchemaHash);
        manifest.AddAsset(materialGuid, "MaterialData", "assets/Materials/test.material.asset", Material.Asset.FileType, Material.Asset.SchemaHash, dependencies: [shaderGuid]);

        IReadOnlyList<AssetEntry> materials = manifest.List(Material.Asset.FileType);

        Assert.Single(materials);
        Assert.Equal(materialGuid, materials[0].AssetGuid);
        Assert.Equal("MaterialData", materials[0].Name);
    }

    private static void AssertEquivalent(IEnumerable<Guid> actual, IEnumerable<Guid> expected)
    {
        Assert.Equal(
            expected.OrderBy(static value => value.ToString(), StringComparer.Ordinal),
            actual.OrderBy(static value => value.ToString(), StringComparer.Ordinal));
    }
}
