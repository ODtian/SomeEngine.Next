using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

return Run(args);

static int Run(string[] args)
{
    if (!TryParse(args, out string sourceArgument, out string profileName))
    {
        PrintUsage();
        return 2;
    }

    try
    {
        string projectRoot = ResolveProjectRoot(Directory.GetCurrentDirectory());
        string sourcePath = Path.IsPathRooted(sourceArgument)
            ? Path.GetFullPath(sourceArgument)
            : Path.GetFullPath(Path.Combine(projectRoot, sourceArgument));
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Shader source '{sourcePath}' does not exist.", sourcePath);
        }

        if (!sourcePath.EndsWith(".slang", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Shader source '{sourcePath}' must have the .slang extension.");
        }

        SlangShaderCookProfile profile = SlangShaderCookProfiles.Resolve(profileName);
        SourceMeta originalMeta = SourceMetaFiles.GetOrCreate(
            sourcePath,
            nameof(SlangShaderImporter));
        if (!string.Equals(
                originalMeta.Importer,
                nameof(SlangShaderImporter),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Source '{sourcePath}' is owned by importer '{originalMeta.Importer}', not "
                    + $"'{nameof(SlangShaderImporter)}'.");
        }

        SourceMeta updatedMeta = new()
        {
            SourceGuid = originalMeta.SourceGuid,
            Importer = originalMeta.Importer,
            ImporterSettings = JsonSerializer.SerializeToElement(
                new SlangShaderImporterSettings { CookProfile = profile.Name },
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                }),
        };
        SourceMetaFiles.Save(sourcePath, updatedMeta);

        using AssetDatabase database = AssetCatalog.CreateDatabase(projectRoot);
        IReadOnlyList<AssetGuid> imported = database.Import(sourcePath);
        if (imported.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one shader asset from '{sourcePath}', but importer returned {imported.Count}.");
        }

        AssetGuid assetGuid = imported[0];
        ShaderAsset asset = database.Load<ShaderAsset>(assetGuid)
            ?? throw new InvalidOperationException(
                $"Shader provider could not load freshly cooked asset '{assetGuid}'.");
        string assetPath = Path.ChangeExtension(sourcePath, ".shader.asset");
        AssetMeta assetMeta = AssetMetaFiles.TryLoad(assetPath)
            ?? throw new InvalidOperationException($"Cooked shader meta '{assetPath}.meta' is missing.");

        int dxilCount = asset.Variants?.Count(static variant =>
            string.Equals(variant.Backend, "dxil", StringComparison.Ordinal)) ?? 0;
        int spirvCount = asset.Variants?.Count(static variant =>
            string.Equals(variant.Backend, "spirv", StringComparison.Ordinal)) ?? 0;
        int reflectionCount = asset.EntryPointReflections?.Count ?? 0;
        if (dxilCount == 0 || spirvCount == 0 || reflectionCount == 0)
        {
            throw new InvalidOperationException(
                $"Cooked shader is incomplete: dxil={dxilCount}, spirv={spirvCount}, "
                    + $"entry-reflections={reflectionCount}.");
        }

        Console.WriteLine($"source={Path.GetRelativePath(projectRoot, sourcePath).Replace('\\', '/')}");
        Console.WriteLine($"source-guid={updatedMeta.SourceGuid}");
        Console.WriteLine($"asset-guid={assetGuid}");
        Console.WriteLine($"cook-profile={profile.Name}");
        Console.WriteLine($"dxil-profile={profile.DxilProfile}");
        Console.WriteLine($"spirv-profile={profile.SpirvProfile}");
        Console.WriteLine($"content-fingerprint={assetMeta.ContentFingerprint}");
        Console.WriteLine($"variants=dxil:{dxilCount},spirv:{spirvCount}");
        Console.WriteLine($"entry-reflections={reflectionCount}");
        Console.WriteLine("provider-load=ok");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

static bool TryParse(string[] args, out string sourcePath, out string profileName)
{
    sourcePath = string.Empty;
    profileName = string.Empty;
    if (args.Length != 4 || !string.Equals(args[0], "shader", StringComparison.Ordinal))
    {
        return false;
    }

    sourcePath = args[1];
    if (!string.Equals(args[2], "--profile", StringComparison.Ordinal))
    {
        return false;
    }

    profileName = args[3];
    return !string.IsNullOrWhiteSpace(sourcePath) && !string.IsNullOrWhiteSpace(profileName);
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: SomeEngine.AssetCook shader <source.slang> --profile <default|d3d12-sm6.2>");
}

static string ResolveProjectRoot(string startDirectory)
{
    string? current = Path.GetFullPath(startDirectory);
    while (!string.IsNullOrEmpty(current))
    {
        if (File.Exists(Path.Combine(current, "SomeEngine.slnx")))
        {
            return current;
        }

        current = Path.GetDirectoryName(current);
    }

    throw new DirectoryNotFoundException(
        $"Could not locate SomeEngine.slnx from '{startDirectory}'.");
}
