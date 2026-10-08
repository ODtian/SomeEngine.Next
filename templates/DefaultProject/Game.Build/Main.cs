using Game.Build;

if (args is ["--refresh-scene-shaders", var applicationDirectory, var sceneDirectory, var engineRoot])
{
    await ImportedSceneBuild.RefreshShadersAsync(applicationDirectory, sceneDirectory, engineRoot);
    return;
}

if (args is ["--import-scene", var application, var source, var output, var engineDirectory])
{
    await ImportedSceneBuild.BuildAsync(application, source, output, engineDirectory);
    return;
}

string? project = null, engine = null;
for (int index = 0; index < args.Length; index++)
{
    string option = args[index];
    if (option is not ("--project" or "--engine") || ++index == args.Length)
        throw new ArgumentException("Usage: Game.Build --project <project directory> --engine <engine checkout>");
    if (option == "--project") project = Path.GetFullPath(args[index]);
    else engine = Path.GetFullPath(args[index]);
}
if (project is null || engine is null)
    throw new ArgumentException("Both --project and --engine are required; the content build never searches for another project's cache.");
await DefaultContent.BuildAsync(project, engine);
