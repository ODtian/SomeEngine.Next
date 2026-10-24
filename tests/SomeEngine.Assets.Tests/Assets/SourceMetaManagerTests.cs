using SomeEngine.Assets.Importers;
using System.Text.Json;
using SomeEngine.Assets;
using static SomeEngine.Tests.TestProjectPaths;

namespace SomeEngine.Tests.Assets;

public sealed class SourceMetaFilesTests
{
    [Fact]
    public void PartialSettingsKeepTheSuppliedTypedDefaults()
    {
        using var fields = JsonDocument.Parse("{\"require_persistent_element_ids\":false}");
        var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), Importers =
            [new(GltfSourceImporter.SourceImporterId, fields.RootElement.Clone())] };
        GltfImporterSettings settings = metadata.GetSettings(GltfSourceImporter.SourceImporterId,
            new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);
        Assert.False(settings.RequirePersistentElementIds);
        Assert.Equal(new GltfImporterSettings().LitMaterialTemplate, settings.LitMaterialTemplate);
        Assert.Equal("someengine_id", settings.PersistentIdProperty);
    }

    [Fact]
    public void SavedSelectionsKeepTypedSettingsSeparateFromBuildResults()
    {
        string directory = CreateTempDir();
        try
        {
            string path = Path.Combine(directory, "character.gltf");
            File.WriteAllText(path, "{}");
            var settings = new GltfImporterSettings
            {
                LitMaterialTemplate = "assets/Materials/Templates/PbrTemplate.material.asset",
                UnlitMaterialTemplate = "assets/Materials/Templates/UnlitTemplate.material.asset",
            };
            SourceGuid source = SourceGuid.New();
            Guid element = Guid.NewGuid();
            SourceMeta.Save(path, new SourceMeta
            {
                SourceGuid = source, Importers = [new(GltfSourceImporter.SourceImporterId,
                    JsonSerializer.SerializeToElement(settings, GltfSettingsJson.Default.GltfImporterSettings))],
                ElementIds = new() { ["mesh:index:0"] = element },
                Build = new() { ImporterVersion = 41, InputHash = "derived data" },
            });
            SourceMeta loaded = SourceMeta.Load(path);
            Assert.Equal(settings, loaded.GetSettings(GltfSourceImporter.SourceImporterId, new GltfImporterSettings(),
                GltfSettingsJson.Default.GltfImporterSettings));
            Assert.Equal((uint)41, loaded.Build.ImporterVersion);
            using (var metadata = JsonDocument.Parse(File.ReadAllBytes(SourceMeta.GetMetaPath(path))))
            {
                Assert.False(metadata.RootElement.TryGetProperty("input_hash", out _));
                Assert.False(metadata.RootElement.TryGetProperty("outputs", out _));
                Assert.False(metadata.RootElement.TryGetProperty("importer_settings", out _));
            }
            File.Delete(SourceMeta.GetBuildPath(path));
            CheckIdentity();
            File.WriteAllText(SourceMeta.GetBuildPath(path), "invalid derived record");
            CheckIdentity();

            void CheckIdentity()
            {
                SourceMeta value = SourceMeta.Load(path);
                Assert.Equal(source, value.SourceGuid);
                Assert.Equal(element, value.ElementIds["mesh:index:0"]);
                Assert.Empty(value.Build.Outputs);
                Assert.Equal(settings, value.GetSettings(GltfSourceImporter.SourceImporterId, new GltfImporterSettings(),
                    GltfSettingsJson.Default.GltfImporterSettings));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
