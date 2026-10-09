using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderResourceUsageTests
{
    [Fact]
    public async Task ReloadedShaderReportsTheResourcesUsedByItsSelectedEntry()
    {
        using var files = new AssetTestDirectory();
        string source = files.File("usage.slang");
        await File.WriteAllTextAsync(source, """
            RWStructuredBuffer<uint> first : register(u3, space2);
            RWStructuredBuffer<uint> second : register(u5, space2);
            [shader("compute")] [numthreads(1,1,1)]
            void WriteFirst(uint3 id : SV_DispatchThreadID) { first[id.x] = 17; }
            [shader("compute")] [numthreads(1,1,1)]
            void WriteSecond(uint3 id : SV_DispatchThreadID) { second[id.x] = 29; }
            """);
        string path = files.File("usage.shader");
        using (var definition = new ShaderAsset { Name = "Selected resource usage" })
        {
            definition.GetBlock<ShaderSourceBlock>().SourceNames = ["usage.slang"];
            await definition.SaveSourceAsync(path);
        }
        AssetEntry built = await SlangShaderImporter.BuildAsync(path);
        using ShaderAsset asset = await ShaderAsset.OpenAsync(built.Path);
        SessionDesc description = SlangShaderImporter.CreateSessionDescription(path,
            [SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession)[0]]);
        Shader shader = await asset.GetBlock<ShaderBlock>().CreateAsync(asset.GetBlock<ShaderSourceBlock>(),
            SlangShaderImporter.GlobalSession, description, asset.Name);
        Assert.True(shader.IsSlangParameterLocationUsed(0, "WriteFirst", SlangParameterCategory.UnorderedAccess, 2, 3),
            string.Join(", ", shader.GetSlangUsedParameters(0, 0)));
        Assert.False(shader.IsSlangParameterLocationUsed(0, "WriteFirst", SlangParameterCategory.UnorderedAccess, 2, 5));
        Assert.True(shader.IsSlangParameterLocationUsed(0, "WriteSecond", SlangParameterCategory.UnorderedAccess, 2, 5));
        Assert.False(shader.IsSlangParameterLocationUsed(0, "WriteSecond", SlangParameterCategory.UnorderedAccess, 2, 3));
    }
}
