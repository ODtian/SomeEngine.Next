using Game.Build;

if (args.Length != 2 || args[0] != "--engine")
    throw new ArgumentException("Usage: SomeEngine.GenerateDefaultAssets --engine <engine checkout>. This command builds the checked-in default project; independent projects must run their own Game.Build.");

string engine = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(engine, "SomeEngine.slnx")))
    throw new DirectoryNotFoundException("Select the engine checkout containing the default project sources.");

string project = Path.Combine(engine, "templates", "DefaultProject");
await DefaultContent.BuildAsync(project, engine);
