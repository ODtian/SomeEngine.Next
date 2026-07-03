using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests;

public sealed class AssetDatabaseConsumerTests
{
    [Fact]
    public void LoadBySourcePathDoesNotImportUnregisteredSource()
    {
        var root = Directory.CreateTempSubdirectory("someengine-assets-");
        try
        {
            var sourcePath = Path.Combine(root.FullName, "assets", "Shaders", "not-imported.slang");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "[shader(\"compute\")] [numthreads(1,1,1)] void CSMain() {}");

            AssetDatabase db = AssetCatalog.CreateDatabase(root.FullName);

            ShaderAsset? loaded = db.Load<ShaderAsset>("assets/Shaders/not-imported.slang", "shader:main");

            Assert.Null(loaded);
            Assert.Null(db.Resolve("assets/Shaders/not-imported.slang", "shader:main"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
