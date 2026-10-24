using SomeEngine.SceneContent;

if (args.Length != 5)
    throw new ArgumentException("Usage: SomeEngine.RenderScenarios <built-app-directory> <new-output-directory> <phong|factors|materials|wpo-alpha|displacement|sparse-motion> <project-source-directory> <engine-directory>");
await RenderScenarios.GenerateAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2],
    Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
Console.WriteLine(Path.GetFullPath(args[1]));
