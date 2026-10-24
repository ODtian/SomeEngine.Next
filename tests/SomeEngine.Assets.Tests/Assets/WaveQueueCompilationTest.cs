using System.IO;
using System.Linq;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
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
            using var assetFile = ShaderTestCompilation.Compile(slangFile);
            Shader asset = await ShaderTestCompilation.CreateAsync(assetFile);

            int spirvTarget = asset.RequireSlangTarget(SlangCompileTarget.Spirv);
            Assert.True(asset.TryGetSlangEntry(
                spirvTarget,
                "CSMain",
                SlangStage.Compute,
                out EntryPointReflection spirvEntry));
            Assert.Equal(SlangStage.Compute, spirvEntry.Stage);
            Assert.True(
                asset.GetSlangEntryBlob(spirvTarget, spirvEntry) is not null ||
                !asset.GetSlangEntryBytes(spirvTarget, spirvEntry).IsEmpty);

            int dxilTarget = asset.RequireSlangTarget(SlangCompileTarget.Dxil);
            Assert.True(asset.TryGetSlangEntry(
                dxilTarget,
                "CSMain",
                SlangStage.Compute,
                out EntryPointReflection dxilEntry));
            Assert.Equal(SlangStage.Compute, dxilEntry.Stage);
            Assert.True(
                asset.GetSlangEntryBlob(dxilTarget, dxilEntry) is not null ||
                !asset.GetSlangEntryBytes(dxilTarget, dxilEntry).IsEmpty);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
