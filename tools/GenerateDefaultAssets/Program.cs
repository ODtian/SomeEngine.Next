using DefaultGame.Content;

if (args.Length != 2 || args[0] != "--engine")
    throw new ArgumentException("Usage: SomeEngine.GenerateDefaultAssets --engine <engine checkout>.");

string engine = Path.GetFullPath(args[1]);
if (!File.Exists(Path.Combine(engine, "SomeEngine.slnx")))
    throw new DirectoryNotFoundException("Select the engine checkout containing the rendering sample sources.");

string project = Path.Combine(engine, "samples", "DefaultGame");
await SampleContent.BuildAsync(project, engine);
