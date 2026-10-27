using System.Buffers;
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
            SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession));
        Shader shader = await asset.CreateSavedAsync(SlangShaderImporter.GlobalSession, description);
        foreach (var (format, category) in new[]
        {
            (SlangCompileTarget.Dxil, SlangParameterCategory.UnorderedAccess),
            (SlangCompileTarget.Spirv, SlangParameterCategory.DescriptorTableSlot),
        })
        {
            int target = shader.RequireSlangTarget(format);
            Assert.True(shader.IsSlangParameterLocationUsed(target, "WriteFirst", category, 2, 3),
                string.Join(", ", shader.GetSlangUsedParameters(target, 0)));
            Assert.False(shader.IsSlangParameterLocationUsed(target, "WriteFirst", category, 2, 5));
            Assert.True(shader.IsSlangParameterLocationUsed(target, "WriteSecond", category, 2, 5));
            Assert.False(shader.IsSlangParameterLocationUsed(target, "WriteSecond", category, 2, 3));
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedEntryRequiresItsCookedResourceUsage(bool includeUsage)
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("constant-usage.slang");
        await File.WriteAllTextAsync(sourcePath, """
            struct Values { uint value; };
            ConstantBuffer<Values> constants : register(b3, space2);
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")] [numthreads(1,1,1)]
            void ReadConstants() { outputValues[0] = constants.value; }
            """);
        using ShaderAsset built = ShaderTestCompilation.Compile(sourcePath);
        Shader compiled = await ShaderTestCompilation.CreateAsync(built);
        SessionDesc description = ShaderTestCompilation.CreateSessionDescription();
        using (var code = new ShaderBlock
        {
            InputVersions = built.GetBlock<ShaderBlock>().InputVersions.ToList(),
        })
        {
            for (int target = 0; target < compiled.SlangTargetCount; target++)
            for (int entry = 0; entry < compiled.SlangEntryCount; entry++)
            {
                code.SetEntryCode(description, target, entry, compiled.GetSlangEntryBytes(target, entry).ToArray());
                if (includeUsage)
                    code.SetEntryUsage(description, target, entry, compiled.GetSlangUsedParameters(target, entry));
            }
            built.SetBlock(code);
        }
        string savedPath = files.File("constant-usage.shader.asset");
        await built.SaveAsync(savedPath);
        File.Delete(sourcePath);
        using ShaderAsset reopened = await ShaderAsset.OpenAsync(savedPath);
        if (!includeUsage)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.CreateSavedAsync(ShaderTestCompilation.Slang, description).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.CreateAsync(ShaderTestCompilation.Slang, description).AsTask());
            // The first requested target is absent; the second still has code without usage.
            using (var mixed = new ShaderBlock
            {
                InputVersions = reopened.GetBlock<ShaderBlock>().InputVersions.ToList(),
            })
            {
                mixed.SetEntryCode(description, 1, 0, compiled.GetSlangEntryBytes(1, 0).ToArray());
                reopened.SetBlock(mixed);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.CreateAsync(ShaderTestCompilation.Slang, description).AsTask());
            return;
        }
        Shader restored = await reopened.CreateSavedAsync(ShaderTestCompilation.Slang, description);
        Assert.True(restored.IsSlangParameterLocationUsed(0, "ReadConstants",
            SlangParameterCategory.ConstantBuffer, 2, 3));
        Assert.True(restored.IsSlangParameterLocationUsed(1, "ReadConstants",
            SlangParameterCategory.DescriptorTableSlot, 2, 3));
        Assert.NotEmpty(restored.GetSlangEntryBytes(0, 0).ToArray());
        Assert.NotEmpty(restored.GetSlangEntryBytes(1, 0).ToArray());
    }

}
