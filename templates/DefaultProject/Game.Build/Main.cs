using Game.Build;

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
