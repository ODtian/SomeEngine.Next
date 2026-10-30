using SlangShaderSharp;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
string shaderDirectory = Path.Combine(root, "assets", "Shaders");
string inputsDirectory = Path.Combine(shaderDirectory, "Inputs");
Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession slang);
slang.CreateSession(new SessionDesc
{
    Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") }],
    SearchPaths = [shaderDirectory],
    DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
    CompilerOptionEntries = [new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0))],
}, out ISession session);

foreach (string name in new[] { "material_interfaces", "standard_material", "cluster_material", "unlit_material", "cluster_unlit_material", "cluster_transparency" })
{
    string path = Path.Combine(inputsDirectory, name + ".ir.slang");
    IModule? input = session.LoadModuleFromSource(name, path, Slang.CreateBlob(await File.ReadAllBytesAsync(path)),
        out ISlangBlob? diagnostics);
    if (input is null) throw new InvalidDataException(diagnostics?.AsString);
    SlangResult result = input.Serialize(out ISlangBlob serialized);
    if (result.Failed) throw new InvalidDataException($"Slang serialization failed with {result}.");
    await File.WriteAllBytesAsync(Path.Combine(inputsDirectory, name + ".slang-module"), serialized.Buffer.ToArray());
    Console.WriteLine($"Exported {name}: {serialized.GetBufferSize()} bytes");
}

string giPath = Path.Combine(shaderDirectory, "gi_screen_probes.slang");
IModule? gi = session.LoadModuleFromSource("gi_screen_probes", giPath,
    Slang.CreateBlob(await File.ReadAllBytesAsync(giPath)), out ISlangBlob? giDiagnostics);
if (gi is null) throw new InvalidDataException(giDiagnostics?.AsString);
Console.WriteLine("Validated GI shader sources");
