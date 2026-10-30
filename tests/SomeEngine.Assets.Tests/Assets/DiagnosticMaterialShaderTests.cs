using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DiagnosticMaterialShaderTests
{
    [Fact]
    public async Task SavedDiagnosticEntriesKeepGeometryResourceReads()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-DiagnosticMaterial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "standard.shader.asset");
            SessionDesc description = SlangShaderImporter.CreateSessionDescription(path,
                SlangShaderImporter.CreateDefaultTargets(ShaderTestCompilation.Slang));
            using (var definition = new ShaderAsset { Name = "Standard diagnostic" })
            {
                using ShaderSourceBlock inputs = StandardMaterialInputs.Create();
                using ClusterMaterialEntriesBlock entries = ClusterMaterialEntriesBlock.Create();
                foreach (string mode in new[] { "0", "8" })
                {
                    inputs.Specializations.Add(new() { EntryPoint = "ClusterShadeMode", Arguments = [mode] });
                    inputs.Specializations.Add(new() { EntryPoint = "ClusterForwardShade", Arguments = ["0", mode] });
                }
                definition.SetBlock(inputs);
                definition.SetBlock(entries);
                definition.SelectInputs<ShaderSourceBlock>();
                definition.SelectInputs<ClusterMaterialEntriesBlock>();
                var compilationTime = System.Diagnostics.Stopwatch.StartNew();
                await definition.CompileAsync(ShaderTestCompilation.Slang, description);
                Console.WriteLine($"Standard 0/8 compilation: {compilationTime.Elapsed.TotalSeconds:F3} seconds");
                await definition.SaveAsync(path);
            }
            using ShaderAsset asset = await ShaderAsset.OpenAsync(path);
            foreach ((string entryPoint, string[] expressions) in new (string, string[])[]
            {
                ("ClusterShadeMode", ["8"]),
                ("ClusterForwardShade", ["0", "8"]),
            })
            {
                Shader shader = await asset.CreateSavedAsync(ShaderTestCompilation.Slang, description, entryPoint,
                    expressions.Select(SpecializationArg.FromExpression).ToArray());
                foreach (SlangCompileTarget target in new[] { SlangCompileTarget.Spirv, SlangCompileTarget.Dxil })
                {
                    int targetIndex = shader.RequireSlangTarget(target);
                    ShaderParameterResource[] resources = shader.GetSlangResourceLayout(target, entryPoint).Resources.ToArray();
                    Console.WriteLine($"{entryPoint}<{string.Join(",", expressions)}> {target}: " +
                        $"{shader.GetSlangEntryBytes(targetIndex, 0).Length} bytes; " +
                        $"resources={string.Join(",", resources.Where(resource => resource.IsUsed).Select(resource => resource.Name))}");
                    Assert.Contains(resources, resource => resource.IsUsed && resource.Name == "PageHeap");
                    Assert.Contains(resources, resource => resource.IsUsed && resource.Name == "OutputColor");
                    Assert.DoesNotContain(resources, resource => resource.IsUsed &&
                        (resource.Name.StartsWith("VirtualShadow", StringComparison.Ordinal) ||
                         resource.Name.StartsWith("MaterialDecal", StringComparison.Ordinal) ||
                         resource.Name is "LightingViews" or "LightBuffer" or "ClusteredLightGrid" or "LightIndexList" or "LightingCookieAtlas"));
                    Assert.False(shader.GetSlangEntryBytes(targetIndex, 0).IsEmpty);
                    await AssertDiagnosticCoatingReads(shader, targetIndex);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task AssertDiagnosticCoatingReads(Shader shader, int targetIndex)
    {
        IMetadata metadata = await shader.GetSlangEntryMetadataAsync(targetIndex, 0);
        VariableLayoutReflection parameters = shader.GetSlangReflection(targetIndex).GetGlobalParamsVarLayout()!.Value;
        TypeLayoutReflection contents = parameters.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        int coatingTableCount = 0;
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            string name = contents.GetBindingRangeLeafVariable(range).Name;
            if (name is not ("CoatingMaterialTextures" or "CoatingMaterialSamplers")) continue;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint descriptor = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            SlangDescriptorLocation location = SlangDescriptorLocation.FromLayout(parameters, contents, set, descriptor);
            SlangResult result = metadata.IsParameterLocationUsed(location.Category,
                checked((nuint)location.Space), checked((nuint)location.Register), out bool used);
            Assert.True(result.Succeeded);
            Assert.False(used);
            coatingTableCount++;
        }
        Assert.Equal(2, coatingTableCount);
    }
}
