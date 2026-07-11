using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.RenderGraph.Tests;

internal sealed class CookedShaderAssetFixture : IDisposable
{
    public static readonly AssetGuid HelloTriangleGuid = AssetGuid.Parse("40101228-9501-58ff-b81a-08767d408801");

    private readonly string _manifestRoot;
    private readonly AssetDatabase _database;

    public CookedShaderAssetFixture()
    {
        string contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Content"));
        string shaderPath = Path.Combine(contentRoot, "Shaders", "hello_triangle.shader.asset");
        if (!File.Exists(shaderPath))
            throw new FileNotFoundException("The cooked hello-triangle shader was not copied to the explicit test content root.", shaderPath);

        _manifestRoot = Path.Combine(Path.GetTempPath(), "SomeEngine.RenderGraph.Tests", Guid.NewGuid().ToString("N"));
        AssetManifest manifest = new();
        manifest.AddAsset(
            HelloTriangleGuid,
            "hello_triangle",
            "Shaders/hello_triangle.shader.asset",
            nameof(ShaderAsset));
        manifest.Save(_manifestRoot);

        _database = new AssetDatabase(
            contentRoot,
            [new ShaderAssetProvider()],
            Array.Empty<IAssetImporter>(),
            _manifestRoot);
    }

    public ShaderAsset LoadHelloTriangle() =>
        _database.Load<ShaderAsset>(HelloTriangleGuid)
        ?? throw new InvalidDataException("The cooked hello-triangle shader is absent from the explicit asset manifest.");

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_manifestRoot)) Directory.Delete(_manifestRoot, recursive: true);
    }
}
