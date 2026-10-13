using SlangShaderSharp;
using SomeEngine.Resources;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class MaterialShaderCompositionTests
{
    [Theory]
    [InlineData("standard_material.slang", "StandardMaterial")]
    [InlineData("unlit_material.slang", "UnlitMaterial")]
    public async Task EngineMaterialCompilesWithIndependentSurfaceEntry(string sourceFile, string materialType)
    {
        string directory = TestProjectPaths.CreateTempDir();
        try
        {
            string path = Path.Combine(directory, "surface.slang");
            await File.WriteAllTextAsync(path, $$"""
                #include "{{sourceFile}}"

                StructuredBuffer<SurfacePixelInput> PixelInputs;
                StructuredBuffer<SurfaceEval> Surfaces;
                StructuredBuffer<Triangle> Triangles;
                RWTexture2DArray<float4> Color;

                [shader("compute")]
                [numthreads(1, 1, 1)]
                void Shade(uniform {{materialType}} material, uint3 thread : SV_DispatchThreadID)
                {
                    material.evaluateSurface(PixelInputs[thread.x], Surfaces[thread.x],
                        float3(1, 0, 0), Triangles[thread.x], Color);
                }
                """);

            using var asset = ShaderTestCompilation.Compile(path);
            Shader shader = await ShaderTestCompilation.CreateAsync(asset);
            for (int target = 0; target < shader.SlangTargetCount; target++)
            {
                Assert.True(shader.TryGetSlangEntry(target, "Shade", SlangStage.Compute, out var entry));
                Assert.True(shader.GetSlangEntryBlob(target, entry)?.GetBufferSize() > 0 ||
                    !shader.GetSlangEntryBytes(target, entry).IsEmpty);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
