using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Tests;

internal static class CookedShaderTestProgram
{
    private static readonly Lazy<CookedShaderProgram> s_program = new(Load);

    internal static CookedShaderProgram Create()
    {
        CookedShaderProgram source = s_program.Value;
        return new CookedShaderProgram
        {
            Backend = source.Backend,
            Data = source.Data!.Value.ToArray(),
            ContentHash = source.ContentHash,
        };
    }

    private static CookedShaderProgram Load()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SomeEngine.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Asset test content was not found.");
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root.FullName, "Library", "AssetManifest"));
        AssetManifestRecord record = manifest.List(AssetType<Shader>.Name).Single(value => value.Name == "hello_triangle");
        var loader = new AssetLoader(new LooseAssetStorage(root.FullName, manifest));
        Shader shader;
        try { shader = loader.LoadAsync(new AssetId<Shader>(record.Guid)).AsTask().GetAwaiter().GetResult(); }
        finally { loader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        return shader.Programs!.Single(program =>
            string.Equals(program.Backend, "spirv", StringComparison.Ordinal));
    }
    internal static Shader Load(Shader value)
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-shader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = new AssetProject(directory, []);
            AssetGuid guid = project.CreateAsset("test.shader.asset", value);
            var loader = new AssetLoader(project.CreateStorage());
            try { return loader.LoadAsync(new AssetId<Shader>(guid)).AsTask().GetAwaiter().GetResult(); }
            finally { loader.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
