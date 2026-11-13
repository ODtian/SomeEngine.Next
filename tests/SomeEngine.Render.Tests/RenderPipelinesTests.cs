using SlangShaderSharp;
using System.Buffers;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Resources;
using Xunit;

namespace SomeEngine.Render.Tests;

public sealed class RenderPipelinesTests
{
    [Fact]
    public async Task EqualComputeDescriptionsShareTheSameTaskAndPipeline()
    {
        using var device = new PipelineTestDevice();
        using var pipelines = new RenderPipelines(device.Backend, device.Device, device.CacheDirectory);
        ComputePipelineDesc description = device.Description;

        Task<Pipeline> first = pipelines.CreateCompute(description);
        Task<Pipeline> second = pipelines.CreateCompute(description with
        {
            Code = ReadOnlySequence<byte>.Empty,
            Label = "Second use",
        });

        Assert.Same(first, second);
        Assert.Same(await first, await second);
    }

    [Fact]
    public async Task SavedComputePipelinesAreReadFromTheDeviceCache()
    {
        using var device = new PipelineTestDevice();
        using (var pipelines = new RenderPipelines(device.Backend, device.Device, device.CacheDirectory))
            await pipelines.CreateCompute(device.Description);

        Assert.True(device.Backend.TryGetCapability(device.Device, out D3D12Diagnostics? diagnostics));
        long hits = diagnostics!.PipelineCreation.CacheLookupHitCount;
        using var reopened = new RenderPipelines(device.Backend, device.Device, device.CacheDirectory);
        await reopened.CreateCompute(device.Description);

        Assert.Equal(hits + 1, diagnostics.PipelineCreation.CacheLookupHitCount);
    }

    private sealed class PipelineTestDevice : IDisposable
    {
        private readonly ShaderLibrary _shaders;
        private readonly Shader _shader;

        internal PipelineTestDevice()
        {
            Backend = D3D12GraphicsBackend.Create();
            var options = new AdapterEnumerationOptions(IncludeSoftware: true);
            Backend.TryEnumerateAdapters(options, [], out int count);
            AdapterInfo[] adapters = new AdapterInfo[count];
            Backend.TryEnumerateAdapters(options, adapters, out _);
            AdapterInfo adapter = adapters.First(value => !value.HardwareAccelerated);
            Device = Backend.CreateDevice(new DeviceDesc(adapter.Id, [new DeviceQueueDesc(QueueType.Compute)]));
            _shaders = new ShaderLibrary(Device.Capabilities.CompileTarget);
            _shader = _shaders.CreateAsync("Pipeline cache test", "pipeline_cache_test.slang", """
                RWStructuredBuffer<uint> result;
                [shader("compute")]
                [numthreads(1, 1, 1)]
                void main(uint3 id : SV_DispatchThreadID) { result[id.x] = 17; }
                """).AsTask().GetAwaiter().GetResult();
            CacheDirectory = Path.Combine(Path.GetTempPath(), "SomeEngine-PipelineCache-" + Guid.NewGuid().ToString("N"));
        }

        internal IGraphicsBackend Backend { get; }
        internal Device Device { get; }
        internal string CacheDirectory { get; }
        internal ComputePipelineDesc Description
        {
            get
            {
                int target = _shader.RequireSlangTarget(Device.Capabilities.CompileTarget);
                EntryPointReflection entry = _shader.RequireSlangEntry(target, "main", SlangStage.Compute);
                return new ComputePipelineDesc(_shader.SlangCode, _shader.GetSlangReflection(target), entry,
                    _shader.GetSlangEntryBytes(target, entry), "Shared compute pipeline",
                    CodeBlob: _shader.GetSlangEntryBlob(target, entry));
            }
        }

        public void Dispose()
        {
            _shaders.Dispose();
            Device.Dispose();
            Backend.Dispose();
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
        }
    }
}
