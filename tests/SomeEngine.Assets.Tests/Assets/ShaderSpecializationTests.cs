using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderSpecializationTests
{
    [Fact]
    public async Task CookedSpecializationsRestoreTheirCodeAndSelectedResourcesWithoutSourceFiles()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("specialized.slang");
        string definitionPath = files.File("specialized.shader");
        await File.WriteAllTextAsync(sourcePath, """
            RWStructuredBuffer<uint> first : register(u3, space2);
            RWStructuredBuffer<uint> second : register(u5, space2);
            RWStructuredBuffer<uint> ordinary : register(u7, space2);

            [shader("compute")] [numthreads(1, 1, 1)]
            void WriteSelected<let useFirst : bool, let useSecond : bool>()
            {
                if (useFirst) first[0] = 17;
                if (useSecond) second[0] = 29;
            }
            [shader("compute")] [numthreads(1, 1, 1)]
            void WriteReverse<let useFirst : bool>()
            {
                if (useFirst) second[0] = 31;
                else first[0] = 43;
            }
            [shader("compute")] [numthreads(1, 1, 1)]
            void WriteOrdinary() { ordinary[0] = 47; }
            """);
        using (var definition = new ShaderAsset { Name = "Specialized resources" })
        {
            ShaderSourceBlock source = definition.GetBlock<ShaderSourceBlock>();
            source.SourceNames = ["specialized.slang"];
            source.Specializations =
            [
                new ShaderSpecialization { EntryPoint = "WriteSelected", Arguments = ["true", "false"] },
                new ShaderSpecialization { EntryPoint = "WriteSelected", Arguments = ["false", "true"] },
                new ShaderSpecialization { EntryPoint = "WriteReverse", Arguments = ["true"] },
                new ShaderSpecialization { EntryPoint = "WriteReverse", Arguments = ["false"] },
            ];
            await definition.SaveSourceAsync(definitionPath);
        }

        AssetEntry built = await SlangShaderImporter.BuildAsync(definitionPath);
        File.Delete(sourcePath);
        File.Delete(definitionPath);

        using ShaderAsset asset = await ShaderAsset.OpenAsync(built.Path);
        SessionDesc description = SlangShaderImporter.CreateSessionDescription(definitionPath,
            SlangShaderImporter.CreateDefaultTargets(SlangShaderImporter.GlobalSession));
        Shader shader = await asset.GetBlock<ShaderBlock>().CreateAsync(asset.GetBlock<ShaderSourceBlock>(),
            SlangShaderImporter.GlobalSession, description, asset.Name);
        Assert.True(shader.HasSpecializations("WriteSelected"));
        Assert.True(shader.HasSpecializations("WriteReverse"));
        Assert.False(shader.HasSpecializations("WriteOrdinary"));
        Assert.True(shader.TryGetSlangEntry(0, "WriteSelected", SlangStage.Compute, out _));
        Shader first = shader.RequireSpecialization("WriteSelected", ["true", "false"]);
        Shader second = shader.RequireSpecialization("WriteSelected", ["false", "true"]);
        Assert.Same(first, shader.RequireSpecialization("WriteSelected", ["true", "false"]));
        Shader reverse = shader.RequireSpecialization("WriteReverse", ["true"]);

        for (int target = 0; target < first.SlangTargetCount; target++)
        {
            EntryPointReflection ordinary = shader.RequireSlangEntry(target, "WriteOrdinary", SlangStage.Compute);
            Assert.Null(shader.GetSlangEntryBlob(target, ordinary));
            Assert.NotEmpty(shader.GetSlangEntryBytes(target, ordinary).ToArray());
            Assert.NotEmpty(reverse.GetSlangEntryBytes(target,
                reverse.RequireSlangEntry(target, "WriteReverse", SlangStage.Compute)).ToArray());
            EntryPointReflection entry = first.RequireSlangEntry(target, "WriteSelected", SlangStage.Compute);
            Assert.Equal(entry.Stage, second.RequireSlangEntry(target, entry.Name, SlangStage.Compute).Stage);
            Assert.Null(first.GetSlangEntryBlob(target, entry));
            Assert.Null(second.GetSlangEntryBlob(target, 0));
            Assert.NotEmpty(first.GetSlangEntryBytes(target, entry).ToArray());
            Assert.NotEmpty(second.GetSlangEntryBytes(target, 0).ToArray());
            Assert.False(first.GetSlangEntryBytes(target, entry).ToArray().AsSpan()
                .SequenceEqual(second.GetSlangEntryBytes(target, 0).ToArray()));
        }

        Assert.True(shader.IsSlangParameterLocationUsed(0, "WriteOrdinary", SlangParameterCategory.UnorderedAccess, 2, 7));
        Assert.False(reverse.IsSlangParameterLocationUsed(0, "WriteReverse", SlangParameterCategory.UnorderedAccess, 2, 3));
        Assert.True(reverse.IsSlangParameterLocationUsed(0, "WriteReverse", SlangParameterCategory.UnorderedAccess, 2, 5));
        Assert.True(first.IsSlangParameterLocationUsed(0, "WriteSelected", SlangParameterCategory.UnorderedAccess, 2, 3));
        Assert.False(first.IsSlangParameterLocationUsed(0, "WriteSelected", SlangParameterCategory.UnorderedAccess, 2, 5));
        Assert.False(second.IsSlangParameterLocationUsed(0, "WriteSelected", SlangParameterCategory.UnorderedAccess, 2, 3));
        Assert.True(second.IsSlangParameterLocationUsed(0, "WriteSelected", SlangParameterCategory.UnorderedAccess, 2, 5));
        Assert.True(first.IsSlangParameterLocationUsed(1, "WriteSelected", SlangParameterCategory.DescriptorTableSlot, 2, 3));
        Assert.False(first.IsSlangParameterLocationUsed(1, "WriteSelected", SlangParameterCategory.DescriptorTableSlot, 2, 5));
        Assert.False(second.IsSlangParameterLocationUsed(1, "WriteSelected", SlangParameterCategory.DescriptorTableSlot, 2, 3));
        Assert.True(second.IsSlangParameterLocationUsed(1, "WriteSelected", SlangParameterCategory.DescriptorTableSlot, 2, 5));
    }
}
