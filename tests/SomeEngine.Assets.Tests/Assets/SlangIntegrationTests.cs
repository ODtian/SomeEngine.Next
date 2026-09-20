using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
namespace SomeEngine.Assets.Tests.Assets;

public class SlangIntegrationTests
{
    [Fact]
    public async Task TestSlangCompilation()
    {
        string source = @"
            [shader(""vertex"")]
            float4 vertexMain(float4 pos : POSITION) : SV_Position
            {
                return pos;
            }

            [shader(""pixel"")]
            float4 pixelMain() : SV_Target
            {
                return float4(1.0, 0.0, 0.0, 1.0);
            }
        ";

        using var directory = new AssetTestDirectory();
        string slangFile = directory.File("compiled.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            using var assetFile = ShaderTestCompilation.Compile(slangFile);
            using Shader asset = await assetFile.GetBlock<ShaderBlock>().CreateAsync(assetFile.Name);

            Assert.NotNull(asset);
            Assert.Equal(Path.GetFileNameWithoutExtension(slangFile), asset.Name);

            // DXIL depends on local DXC availability; SPIR-V must always compile.
            Assert.True(asset.Programs!.Count >= 2);

            Assert.True(asset.TryEntry(
                "spirv",
                "vertexMain",
                ShaderStage.Vertex,
                out _,
                out var vsSpirv));
            Assert.True(vsSpirv.Code.Length > 0);

            Assert.True(asset.TryEntry(
                "spirv",
                "pixelMain",
                ShaderStage.Pixel,
                out _,
                out var psSpirv));
            Assert.True(psSpirv.Code.Length > 0);
        }
        finally
        {
            if (File.Exists(slangFile)) File.Delete(slangFile);
        }
    }
    [Fact]
    public async Task TestSlangReflection()
    {
        string source = @"
            struct Params {
                float4 color;
            };

            [[vk::binding(0, 0)]]
            ConstantBuffer<Params> gParams;

            [[vk::binding(1, 0)]]
            Texture2D gTexture;

            [shader(""pixel"")]
            float4 main() : SV_Target
            {
                return gTexture.Load(int3(0, 0, 0)) * gParams.color;
            }
        ";

        using var directory = new AssetTestDirectory();
        string tempFile = directory.File("test_reflection.slang");
        File.WriteAllText(tempFile, source);

        try
        {
            using var assetFile = ShaderTestCompilation.Compile(tempFile);
            using Shader asset = await assetFile.GetBlock<ShaderBlock>().CreateAsync(assetFile.Name);

            var reflection = asset.Reflections?.FirstOrDefault()?.Reflection;
            Assert.NotNull(reflection);
            Assert.NotNull(reflection!.Resources);

            // Check gParams
            var gParams = reflection.Resources!.FirstOrDefault(r => r.Name == "gParams");
            Assert.NotNull(gParams);
            Assert.True((gParams.Stages & 0x02) != 0, "Should be visible in Pixel stage (0x02)");

            // Check gTexture
            var gTexture = reflection.Resources.FirstOrDefault(r => r.Name == "gTexture");
            Assert.NotNull(gTexture);
            Assert.True((gTexture!.Stages & 0x02) != 0, "Should be visible in Pixel stage (0x02)");

            // Print all resources for manual verification
            Console.WriteLine($"--- Layout for {asset.Name} ---");
            foreach (var r in reflection.Resources)
            {
                Console.WriteLine($"  Name: {r.Name}, Stages: 0x{r.Stages:X}");
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task NativeDefinitionRetainsIdentityWithoutCreatingSourceCompanions()
    {
        string source = @"
            [shader(""compute"")]
            [numthreads(1,1,1)]
            void main() {}
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string slangFile = Path.Combine(dir, "stable_guid.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            string definitionPath = Path.Combine(dir, "selected.shader");
            using var definition = ShaderTestCompilation.Define(slangFile);
            await definition.SaveSourceAsync(definitionPath);
            using var asset1File = SlangShaderImporter.CompileDefinition(definition, definitionPath);
            using Shader asset1 = await asset1File.GetBlock<ShaderBlock>().CreateAsync(asset1File.Name);
            using var reloaded = await ShaderAsset.OpenSourceAsync(definitionPath);
            using var asset2File = SlangShaderImporter.CompileDefinition(reloaded, definitionPath);
            using Shader asset2 = await asset2File.GetBlock<ShaderBlock>().CreateAsync(asset2File.Name);

            Guid guid1 = asset1File.Id;
            Guid guid2 = asset2File.Id;
            Assert.Equal(guid2, guid1);
            Assert.Equal(definition.Id, guid1);
            Assert.Empty(Directory.EnumerateFiles(dir, "*.meta"));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.asset"));
            Assert.NotNull(asset1File.GetBlock<ShaderBlock>().ImportTrace);
            Assert.Equal(definitionPath, asset1File.GetBlock<ShaderBlock>().ImportTrace!.SourcePath);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task TestSlangImport_TracksIncludeDependencies_AndKeepsAssetGuidStable()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        string includeFile = Path.Combine(dir, "common_inc.slang");
        string sourceFile = Path.Combine(dir, "with_include.slang");

        File.WriteAllText(includeFile, "float4 GetColor() { return float4(1, 0, 0, 1); }");
        File.WriteAllText(sourceFile, "#include \"common_inc.slang\"\n[shader(\"pixel\")] float4 main() : SV_Target { return GetColor(); }");

        try
        {
            using var definition = ShaderTestCompilation.Define(sourceFile);
            using var asset1File = ShaderTestCompilation.Compile(definition, sourceFile);
            using Shader asset1 = await asset1File.GetBlock<ShaderBlock>().CreateAsync(asset1File.Name);
            Guid guid1 = asset1File.Id;
            var trace1 = asset1File.GetBlock<ShaderBlock>().ImportTrace;
            Assert.NotNull(trace1);
            Assert.NotNull(trace1.Dependencies);
            IList<DependencyEntry> dependencies1 = trace1.Dependencies;
            Assert.True(dependencies1.Count >= 2);
            Assert.Contains(dependencies1, d => d.Path == "common_inc.slang" || d.Path!.EndsWith("/common_inc.slang"));

            string inputHash1 = trace1.ContentFingerprint!;

            File.WriteAllText(includeFile, "float4 GetColor() { return float4(0, 1, 0, 1); }");

            using var asset2File = ShaderTestCompilation.Compile(definition, sourceFile);

            using Shader asset2 = await asset2File.GetBlock<ShaderBlock>().CreateAsync(asset2File.Name);
            Guid guid2 = asset2File.Id;
            Assert.Equal(guid1, guid2);
            var trace2 = asset2File.GetBlock<ShaderBlock>().ImportTrace;
            Assert.NotNull(trace2);
            Assert.NotEqual(inputHash1, trace2.ContentFingerprint);
            Assert.NotNull(trace2.Dependencies);
            IList<DependencyEntry> dependencies2 = trace2.Dependencies;
            Assert.Contains(dependencies2, d => d.Path == "common_inc.slang" || d.Path!.EndsWith("/common_inc.slang"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public async Task ContentHash_IdenticalLogicInDifferentFiles_ProducesSameHash()
    {
        // 两个不同文件，相同的 compute shader 逻辑 → content hash 应该一致
        string source = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "shader_a.slang");
        string fileB = Path.Combine(dir, "shader_b.slang");
        File.WriteAllText(fileA, source);
        File.WriteAllText(fileB, source);

        try
        {
            using var assetAFile = ShaderTestCompilation.Compile(fileA);
            using Shader assetA = await assetAFile.GetBlock<ShaderBlock>().CreateAsync(assetAFile.Name);
            using var assetBFile = ShaderTestCompilation.Compile(fileB);
            using Shader assetB = await assetBFile.GetBlock<ShaderBlock>().CreateAsync(assetBFile.Name);

            Assert.NotNull(assetA.Programs);
            Assert.NotNull(assetB.Programs);

            // 验证每个 backend 的 content hash 都一致
            var hashesA = assetA.Programs!.Select(program => { Assert.NotNull(program); return program; })
                .ToDictionary(
                    program => program.Backend!,
                    program => EntryHash(assetA, program.Backend!, "CSMain", ShaderStage.Compute));
            var hashesB = assetB.Programs!.Select(program => { Assert.NotNull(program); return program; })
                .ToDictionary(
                    program => program.Backend!,
                    program => EntryHash(assetB, program.Backend!, "CSMain", ShaderStage.Compute));

            Assert.True(hashesA.Count > 0, "Should have at least one CSMain variant");

            foreach (var kvp in hashesA)
            {
                Assert.True(hashesB.ContainsKey(kvp.Key), $"Backend {kvp.Key} missing from shader_b");
                Assert.Equal(kvp.Value, hashesB[kvp.Key]);
                Assert.NotNull(kvp.Value);
                Assert.NotEmpty(kvp.Value!);

                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ContentHash_DifferentLogic_ProducesDifferentHash()
    {
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 3.0 + 1.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "shader_same.slang");
        string fileB = Path.Combine(dir, "shader_diff.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            using var assetAFile = ShaderTestCompilation.Compile(fileA);
            using Shader assetA = await assetAFile.GetBlock<ShaderBlock>().CreateAsync(assetAFile.Name);
            using var assetBFile = ShaderTestCompilation.Compile(fileB);
            using Shader assetB = await assetBFile.GetBlock<ShaderBlock>().CreateAsync(assetBFile.Name);

            string spirvA = EntryHash(assetA, "spirv", "CSMain", ShaderStage.Compute);
            string spirvB = EntryHash(assetB, "spirv", "CSMain", ShaderStage.Compute);

            Assert.NotEqual(spirvB, spirvA);

            Console.WriteLine($"  A: {spirvA}");
            Console.WriteLine($"  B: {spirvB}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CookedProgramHash_CoversEachDetachedBackendPayload()
    {
        // Backend compilers may preserve or erase local names independently. The asset hash
        // always covers the exact detached payload that runtime pipeline creation consumes.
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                float value = float(tid.x) * 2.0;
                outputBuffer[tid.x] = value;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                float result = float(tid.x) * 2.0;
                outputBuffer[tid.x] = result;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "varname_a.slang");
        string fileB = Path.Combine(dir, "varname_b.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            using var assetAFile = ShaderTestCompilation.Compile(fileA);
            using Shader assetA = await assetAFile.GetBlock<ShaderBlock>().CreateAsync(assetAFile.Name);
            using var assetBFile = ShaderTestCompilation.Compile(fileB);
            using Shader assetB = await assetBFile.GetBlock<ShaderBlock>().CreateAsync(assetBFile.Name);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                CookedShaderProgram programA = assetAFile.GetBlock<ShaderBlock>().Programs.Single(program =>
                    string.Equals(program.Backend, backend, StringComparison.Ordinal));
                CookedShaderProgram programB = assetBFile.GetBlock<ShaderBlock>().Programs.Single(program =>
                    string.Equals(program.Backend, backend, StringComparison.Ordinal));
                using var bytesA = await assetAFile.GetBlock<ShaderBlock>().AcquireBufferAsync(programA.DataChunkKey);
                using var bytesB = await assetBFile.GetBlock<ShaderBlock>().AcquireBufferAsync(programB.DataChunkKey);
                using var readA = bytesA.Retain();
                using var readB = bytesB.Retain();
                string hashA = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(readA.ToArray()));
                string hashB = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(readB.ToArray()));

                Console.WriteLine($"  [{backend}] A: {hashA}");
                Console.WriteLine($"  [{backend}] B: {hashB}");
                Assert.Equal(programA.ContentHash, hashA);
                Assert.Equal(programB.ContentHash, hashB);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ContentHash_UnusedExtraBuffer_SameHash()
    {
        // 一个文件多了一个未使用的 buffer 声明 → DCE 应剥离 → hash 相同
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;
            RWStructuredBuffer<float> unusedBuffer;  // 额外声明，但未使用

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "no_extra.slang");
        string fileB = Path.Combine(dir, "with_extra.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            using var assetAFile = ShaderTestCompilation.Compile(fileA);
            using Shader assetA = await assetAFile.GetBlock<ShaderBlock>().CreateAsync(assetAFile.Name);
            using var assetBFile = ShaderTestCompilation.Compile(fileB);
            using Shader assetB = await assetBFile.GetBlock<ShaderBlock>().CreateAsync(assetBFile.Name);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                string hashA = EntryHash(assetA, backend, "CSMain", ShaderStage.Compute);
                string hashB = EntryHash(assetB, backend, "CSMain", ShaderStage.Compute);

                Console.WriteLine($"  [{backend}] A: {hashA}");
                Console.WriteLine($"  [{backend}] B: {hashB}");
                Assert.Equal(hashB, hashA);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ContentHash_DifferentUnusedBufferLayout_SameHash()
    {
        // 两个文件都有一个未使用的 buffer，但布局不同 → DCE 剥离后 hash 应相同
        string sourceA = @"
            RWStructuredBuffer<float> outputBuffer;

            struct ExtraA { float x; float y; };
            RWStructuredBuffer<ExtraA> unusedBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string sourceB = @"
            RWStructuredBuffer<float> outputBuffer;

            struct ExtraB { int a; int b; int c; };
            RWStructuredBuffer<ExtraB> unusedBuffer;

            [shader(""compute"")]
            [numthreads(64,1,1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                outputBuffer[tid.x] = float(tid.x) * 2.0;
            }
        ";

        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string fileA = Path.Combine(dir, "layout_a.slang");
        string fileB = Path.Combine(dir, "layout_b.slang");
        File.WriteAllText(fileA, sourceA);
        File.WriteAllText(fileB, sourceB);

        try
        {
            using var assetAFile = ShaderTestCompilation.Compile(fileA);
            using Shader assetA = await assetAFile.GetBlock<ShaderBlock>().CreateAsync(assetAFile.Name);
            using var assetBFile = ShaderTestCompilation.Compile(fileB);
            using Shader assetB = await assetBFile.GetBlock<ShaderBlock>().CreateAsync(assetBFile.Name);

            foreach (var backend in new[] { "spirv", "dxil" })
            {
                string hashA = EntryHash(assetA, backend, "CSMain", ShaderStage.Compute);
                string hashB = EntryHash(assetB, backend, "CSMain", ShaderStage.Compute);

                Console.WriteLine($"  [{backend}] A: {hashA}");
                Console.WriteLine($"  [{backend}] B: {hashB}");
                Assert.Equal(hashB, hashA);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static string EntryHash(
        Shader asset,
        string backend,
        string entryPoint,
        ShaderStage stage)
    {
        Assert.True(asset.TryEntry(backend, entryPoint, stage, out _, out var entry));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(entry.Code.Span));
    }
}
