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
            AssetGuid shaderGuid = AssetGuid.New();
            AssetGuid materialGuid = AssetGuid.New();

            AssetManifest manifest = new();
            manifest.AddSource(sourceGuid, "assets/Shaders/test.slang");
            manifest.AddAsset(shaderGuid, "TestShader", "assets/Shaders/test.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, sourceGuid, "shader:main");
            manifest.AddAsset(materialGuid, "TestMaterial", "assets/Materials/test.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [shaderGuid]);
            manifest.Save(dir);

            AssetManifest loaded = AssetManifest.Load(dir);

            Assert.True(loaded.TrySourcePath(sourceGuid, out string? sourcePath));
            Assert.Equal("assets/Shaders/test.slang", sourcePath);
            Assert.True(loaded.TryGetAsset(shaderGuid, out AssetManifestRecord shaderRecord));
            Assert.Equal("TestShader", shaderRecord.Name);
            Assert.Equal(sourceGuid, shaderRecord.SourceGuid);
            Assert.Equal(Shader.SchemaFingerprint, shaderRecord.SchemaFingerprint);
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
                AssetGuid.New(),
                "Probe",
                "assets/probes/value.probe.asset",
                "ProbeAsset",
                schemaFingerprint: 0));
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
            AssetGuid guid = AssetGuid.New();
            var entry = new JsonObject
            {
                ["asset_guid"] = guid.ToFlatString(),
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
        AssetGuid mainGuid = AssetGuid.New();
        AssetGuid shadowGuid = AssetGuid.New();

        AssetManifest manifest = new();
        manifest.AddSource(sourceGuid, "assets/Shaders/shared.slang");
        manifest.AddAsset(mainGuid, "Main", "assets/Shaders/shared.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, sourceGuid, "shader:main");
        manifest.AddAsset(shadowGuid, "Shadow", "assets/Shaders/shared.shadow.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint, sourceGuid, "shader:shadow");

        Assert.True(manifest.TrySourceGuid("assets/Shaders/shared.slang", out SourceGuid resolvedSourceGuid));
        Assert.Equal(sourceGuid, resolvedSourceGuid);
        Assert.True(manifest.TryAssetPath("assets/Shaders/shared.shader.asset", out AssetManifestRecord assetRecord));
        Assert.Equal(mainGuid, assetRecord.Guid);
        Assert.True(manifest.TrySourceAsset(sourceGuid, "shader:shadow", out AssetManifestRecord subAssetRecord));
        Assert.Equal(shadowGuid, subAssetRecord.Guid);
        AssertEquivalent(manifest.AssetsBySource(sourceGuid), new[] { mainGuid, shadowGuid });
    }

    [Fact]
    public void GetReferencers_TracksIncomingDependencies()
    {
        AssetGuid shaderGuid = AssetGuid.New();
        AssetGuid materialGuid = AssetGuid.New();
        AssetGuid instanceGuid = AssetGuid.New();
        AssetGuid meshGuid = AssetGuid.New();

        AssetManifest manifest = new();
        manifest.AddAsset(shaderGuid, "Shader", "assets/Shaders/test.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint);
        manifest.AddAsset(materialGuid, "Material", "assets/Materials/test.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [shaderGuid]);
        manifest.AddAsset(instanceGuid, "Instance", "assets/Materials/test.materialinstance.asset", AssetType<MaterialInstance>.Name, MaterialInstance.SchemaFingerprint, dependencies: [materialGuid]);
        manifest.AddAsset(meshGuid, "Mesh", "assets/Meshes/test.mesh.asset", AssetType<Mesh>.Name, Mesh.SchemaFingerprint, dependencies: [materialGuid]);

        Assert.Equal(new[] { materialGuid }, manifest.GetReferencers(shaderGuid));
        AssertEquivalent(manifest.GetReferencers(materialGuid), new[] { instanceGuid, meshGuid });
        Assert.Empty(manifest.GetReferencers(instanceGuid));
    }

    [Fact]
    public void List_CanFilterByAssetType()
    {
        AssetGuid shaderGuid = AssetGuid.New();
        AssetGuid materialGuid = AssetGuid.New();

        AssetManifest manifest = new();
        manifest.AddAsset(shaderGuid, "Shader", "assets/Shaders/test.shader.asset", AssetType<Shader>.Name, Shader.SchemaFingerprint);
        manifest.AddAsset(materialGuid, "Material", "assets/Materials/test.material.asset", AssetType<Material>.Name, Material.SchemaFingerprint, dependencies: [shaderGuid]);

        IReadOnlyList<AssetManifestRecord> materials = manifest.List(AssetType<Material>.Name);

        Assert.Single(materials);
        Assert.Equal(materialGuid, materials[0].Guid);
        Assert.Equal("Material", materials[0].Name);
    }

    private static void AssertEquivalent(IEnumerable<AssetGuid> actual, IEnumerable<AssetGuid> expected)
    {
        Assert.Equal(
            expected.OrderBy(static value => value.ToString(), StringComparer.Ordinal),
            actual.OrderBy(static value => value.ToString(), StringComparer.Ordinal));
    }
}
