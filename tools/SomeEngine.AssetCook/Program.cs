using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length < 2 || args[0] != "shader" || (args.Length - 2) % 2 != 0)
    {
        Console.Error.WriteLine("Usage: SomeEngine.AssetCook shader <definition.shader> [--project <directory>] [--include <directory>]...");
        return 2;
    }
    try
    {
        string projectRoot = Directory.GetCurrentDirectory();
        var includes = new List<string>();
        for (int i = 2; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--project": projectRoot = Path.GetFullPath(args[i + 1]); break;
                case "--include": includes.Add(args[i + 1]); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        string sourcePath = Path.GetFullPath(Path.Combine(projectRoot, args[1]));
        var project = new AssetProject(projectRoot, [], sourceFormats: [new ShaderSourceFormat(includes)]);
        Guid id = await project.BuildAsync(sourcePath);
        await using var loader = new AssetLoader(project.CreateStorage());
        using ShaderAsset file = await ShaderAsset.OpenAsync(loader, new AssetId<ShaderAsset>(id));
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc[] targets = SlangShaderImporter.CreateDefaultTargets(slang);
        SessionDesc sessionDescription = SlangShaderImporter.CreateSessionDescription(
            sourcePath,
            targets,
            includes);
        Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(
            file.GetBlock<ShaderSourceBlock>(),
            slang,
            sessionDescription,
            file.Name);
        Console.WriteLine($"source={Path.GetRelativePath(projectRoot, sourcePath).Replace('\\', '/')}");
        Console.WriteLine($"asset-guid={id}");
        Console.WriteLine($"content-hash={file.GetBlock<ShaderBlock>().ImportTrace?.ContentFingerprint}");
        for (int targetIndex = 0; targetIndex < shader.SlangTargetCount; targetIndex++)
        {
            Console.WriteLine(
                $"target={shader.GetSlangTarget(targetIndex).Format} " +
                $"entries={shader.GetSlangReflection(targetIndex).EntryPointCount}");
        }
        Console.WriteLine("storage-open=ok");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}
