using System.IO;
using System.Linq;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public class WaveQueueCompilationTest
{
    [Fact]
    public async Task WaveQueue_CompilesWithTrivialTask()
    {
        // Inline Slang source that includes wave_queue.slang and implements a trivial IWaveTask.
        // Each lane "produces" 1 task that writes lane index to a UAV buffer.
        string waveQueuePath = TestProjectPaths.ShaderPath("wave_queue.slang").Replace('\\', '/');
        string source = $$"""
            #include "{{waveQueuePath}}"

            RWStructuredBuffer<uint> OutputBuffer;

            struct TrivialTask : IWaveTask
            {
                uint value;

                uint GetTaskCount() { return 1; }

                [mutating]
                void ExecuteTask(uint srcLane, uint localIdx, bool bActive)
                {
                    if (!bActive) return;
                    uint v = WaveReadLaneAt(value, srcLane);
                    OutputBuffer[WaveGetLaneIndex()] = v;
                }
            };

            [shader("compute")]
            [numthreads(32, 1, 1)]
            void CSMain(uint3 tid : SV_DispatchThreadID)
            {
                TrivialTask task;
                task.value = tid.x;
                WaveQueue::Distribute(task);
            }
        """;

        string tempDir = Path.Combine(Path.GetTempPath(), "SomeEngine.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string slangFile = Path.Combine(tempDir, "wave_queue_trivial.slang");
        File.WriteAllText(slangFile, source);

        try
        {
            using var assetFile = SlangShaderImporter.Import(slangFile, source).ToAsset();
            Shader asset = await Shader.CreateAsync(assetFile);

            Assert.NotNull(asset);
            Assert.NotNull(asset.Programs);
            Assert.NotEmpty(asset.Programs!);

            // At least SPIR-V should compile. DXIL requires DXC which may not be available.
            Assert.True(asset.TryEntry(
                "spirv",
                "CSMain",
                ShaderStage.Compute,
                out _,
                out var csSpirv));
            Assert.True(csSpirv.Code.Length > 0);

            if (asset.TryEntry(
                "dxil",
                "CSMain",
                ShaderStage.Compute,
                out _,
                out var csDxil))
            {
                Assert.True(csDxil.Code.Length > 0);
            }
            else
            {
                Console.WriteLine("WARNING: DXIL variant not produced (DXC not found). SPIR-V OK.");
            }

            Console.WriteLine($"WaveQueue compilation OK: {asset.Programs.Count} programs");
            foreach (var program in asset.Programs)
            {
                Console.WriteLine(
                    $"  {program.Backend}: {program.Data?.Length ?? 0} bytes, " +
                    $"{program.Program?.EntryPoints.Count ?? 0} entries");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
