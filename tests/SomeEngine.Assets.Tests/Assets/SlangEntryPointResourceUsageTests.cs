using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangEntryPointResourceUsageTests
{
    [Fact]
    public void CookedReflectionContainsOnlyResourcesUsedByTheCompiledEntryPoint()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"someengine-entry-resources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "entry_resources.slang");
        File.WriteAllText(sourcePath, Source);
        try
        {
            Shader shader = SlangShaderImporter.ImportTransient(sourcePath);
            AssertResources(shader, "FirstMain", "FirstInput", "Output");
            AssertResources(shader, "SecondMain", "SecondInput", "Output");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertResources(
        Shader shader,
        string entryPoint,
        string first,
        string second)
    {
        foreach (string backend in new[] { "dxil", "spirv" })
        {
            ShaderEntryPointReflection reflection = shader.EntryPointReflections!.Single(value =>
                string.Equals(value.Backend, backend, StringComparison.Ordinal)
                && string.Equals(value.EntryPoint, entryPoint, StringComparison.Ordinal)
                && value.Stage == ShaderStage.Compute);
            IList<ShaderResourceReflection> resources = reflection.Reflection!.Resources!;
            Assert.Equal(2, resources.Count);
            Assert.Contains(resources, value => string.Equals(value.Name, first, StringComparison.Ordinal));
            Assert.Contains(resources, value => string.Equals(value.Name, second, StringComparison.Ordinal));
        }
    }

    private const string Source = """
        StructuredBuffer<uint> FirstInput;
        StructuredBuffer<uint> SecondInput;
        RWStructuredBuffer<uint> Output;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void FirstMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[dispatchThreadId.x] = FirstInput[dispatchThreadId.x];
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void SecondMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[dispatchThreadId.x] = SecondInput[dispatchThreadId.x];
        }
        """;
}
