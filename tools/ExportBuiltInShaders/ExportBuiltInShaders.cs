using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Packs;
using SomeEngine.Elements.Render;
using SomeEngine.Render.Features.Skinning;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
string temporary = Path.Combine(Path.GetTempPath(), "SomeEngine.ElementShaders-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    IGlobalSession slang = Shader.CreateSlangGlobalSession();
    string elements = Path.Combine(root, "src/SomeEngine.Elements.Render/Shaders");
    await Export(elements, Path.Combine(elements, "Elements.assetpack"),
        ElementShaderSettings.Create(slang, SlangCompileTarget.Dxil, SlangCompileTarget.Spirv),
    [
        ("Surface", new("dbb3851d-b3d9-4740-9800-bd9d39f62431"), "Surface.slang"),
        ("Image", new("41c73bb7-7834-4f80-9eac-5e8cf995d314"), "Image.slang"),
        ("Glyphs", new("8ded2608-93d1-41e3-9fbe-bf90616573fa"), "Glyphs.slang"),
    ]);
    await Export(Path.Combine(root, "assets/Shaders"), Path.Combine(root, "src/SomeEngine.Render.Features/Skinning/Skinning.assetpack"),
        SkinningShaderSettings.Create(slang, SlangCompileTarget.Dxil, SlangCompileTarget.Spirv),
    [
        ("GPU poses", new("e6c76942-d39a-4602-96b9-05737d49460b"), "gpu_pose.slang"),
        ("Skin bounds", new("0727bf41-cd89-4519-8c7e-cda0e4c89aa9"), "skin_bounds.slang"),
    ]);

    async Task Export(string directory, string outputPath, SessionDesc settings, (string Name, Guid Id, string Source)[] shaders)
    {
        settings.SearchPaths = [directory];
        var pack = new AssetPackBuilder();
        foreach (var shader in shaders)
        {
            using var asset = new ShaderAsset(shader.Id) { Name = shader.Name };
            asset.GetBlock<ShaderSourceBlock>().SourceNames = [shader.Source];
            await asset.CompileAsync(slang, settings);
            string path = Path.Combine(temporary, shader.Name + ".shader.asset");
            await asset.SaveAsync(path);
            pack.AddAsset(asset.Id, ShaderAsset.FileType, await File.ReadAllBytesAsync(path), ShaderAsset.SchemaHash);
            Console.WriteLine($"Exported {shader.Name}");
        }
        using var output = File.Create(outputPath);
        await pack.WriteAsync(output);
    }
}
finally
{
    foreach (string path in Directory.EnumerateFiles(temporary)) File.Delete(path);
    Directory.Delete(temporary);
}
