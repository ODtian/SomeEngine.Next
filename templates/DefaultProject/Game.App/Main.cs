using Game.Assets;
using Game.Content;

string manifestPath = Path.Combine(AppContext.BaseDirectory, "Library", "AssetManifest", SomeEngine.Assets.AssetManifest.CatalogFileName);
if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine("Project content has not been built. Run Game.Build for this project, then rebuild Game.App.");
    return 1;
}
MaterialTypes.Register();
return await SomeEngine.Runtime.Application.RunAsync(args, static (material, seconds) =>
{
    if (material is not StandardMaterial standard) return false;
    standard.Roughness = 0.5f + MathF.Sin(seconds) * 0.2f;
    return true;
});
