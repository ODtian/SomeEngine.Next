using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using Buffer = SomeEngine.Graphics.Buffer;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class SoftwareRasterCoverageTests
{
    [Theory]
    [InlineData("D3D12", 32)]
    [InlineData("D3D12", 64)]
    [InlineData("Vulkan", 32)]
    [InlineData("Vulkan", 64)]
    public async Task ShaderLibraryNarrowScissorPreservesTriangleCoverage(string backendName, int threadCount)
    {
        var inputs = new CoverageInputs
        {
            Vertex0 = Clip(200867, 0, 1280, 128),
            Vertex1 = Clip(0, 32641, 1280, 128),
            Vertex2 = Clip(327680, 32639, 1280, 128),
            ViewWidth = 1280,
            ViewHeight = 128,
            ImageWidth = 3,
            ImageHeight = 128,
            ScissorLeft = 639,
            ScissorTop = 0,
            ScissorRight = 642,
            ScissorBottom = 128,
        };
        await AssertTriangleCoverageAsync(inputs, [(200867, 0), (0, 32641), (327680, 32639)], backendName, threadCount);
    }

    [Theory]
    [InlineData("D3D12", 32)]
    [InlineData("D3D12", 64)]
    [InlineData("Vulkan", 32)]
    [InlineData("Vulkan", 64)]
    public async Task ShaderLibraryTallNarrowScissorPreservesCoveredPixel(string backendName, int threadCount)
    {
        var inputs = new CoverageInputs
        {
            Vertex0 = Clip(200867, 0, 1280, 720),
            Vertex1 = Clip(0, 184193, 1280, 720),
            Vertex2 = Clip(327680, 184192, 1280, 720),
            ViewWidth = 1280,
            ViewHeight = 720,
            ImageWidth = 3,
            ImageHeight = 720,
            ScissorLeft = 1276,
            ScissorTop = 0,
            ScissorRight = 1279,
            ScissorBottom = 720,
        };
        // Pixel (1277, 719) is strictly inside all three exact subpixel edges.
        // A narrow row bound must not discard it before the coverage test.
        await AssertTriangleCoverageAsync(inputs, [(200867, 0), (0, 184193), (327680, 184192)], backendName, threadCount);
    }

    private static Vector4 Clip(float x, float y, float width, float height) =>
        new(x / 256f * (2f / width) - 1f, 1f - y / 256f * (2f / height), .5f, 1f);

    private static async Task AssertTriangleCoverageAsync(
        CoverageInputs inputs, (long X, long Y)[] vertices, string backendName, int threadCount)
    {
        byte[] images = await RenderAsync(FrameStageScene.FindRoot(), inputs, backendName, threadCount);
        int width = checked((int)inputs.ImageWidth);
        int height = checked((int)inputs.ImageHeight);
        int imageBytes = checked(width * height * sizeof(uint));
        byte[] actual = images.AsSpan(0, imageBytes).ToArray();
        byte[] rectangle = images.AsSpan(imageBytes, imageBytes).ToArray();
        // Integer subpixel coordinates give an independent geometric reference.
        // The old floating-point visit order may accept a point outside an edge.
        uint[] coverage = new uint[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            long pixelX = (inputs.ScissorLeft + x) * 256L + 128;
            long pixelY = (inputs.ScissorTop + y) * 256L + 128;
            bool inside = true;
            for (int edge = 0; edge < 3; edge++)
            {
                var first = vertices[edge];
                var second = vertices[(edge + 1) % 3];
                long edgeX = first.X - second.X, edgeY = first.Y - second.Y;
                long topLeft = edgeY > 0 || (edgeY == 0 && edgeX <= 0) ? 1 : 0;
                long value = edgeY * (first.X - pixelX) - edgeX * (first.Y - pixelY) - topLeft;
                inside &= value >= 0;
            }
            coverage[y * width + x] = inside ? uint.MaxValue : 0;
        }
        byte[] expected = MemoryMarshal.AsBytes(coverage.AsSpan()).ToArray();
        int oldDifferences = rectangle.Where((value, index) => value != expected[index]).Count();
        Console.WriteLine($"{backendName}, {threadCount} threads, {inputs.ViewWidth}x{inputs.ViewHeight}: old rectangle differs from integer coverage in {oldDifferences / 4} pixels.");
        Assert.Contains(expected, value => value != 0);
        ImageArtifacts.AssertEqual(new(width, height, Format.R8G8B8A8UNorm, expected),
            new(width, height, Format.R8G8B8A8UNorm, actual));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct CoverageInputs
    {
        internal Vector4 Vertex0, Vertex1, Vertex2;
        internal uint ViewWidth, ViewHeight, ImageWidth, ImageHeight;
        internal int ScissorLeft, ScissorTop, ScissorRight, ScissorBottom;
    }

    private static async Task<byte[]> RenderAsync(string root, CoverageInputs inputs, string backendName, int threadCount)
    {
        using IGraphicsBackend backend = new ValidationLayer(backendName == "Vulkan"
            ? VulkanGraphicsBackend.Create() : D3D12GraphicsBackend.Create(),
            new ValidationOptions(NativeValidationDisabled: backendName == "Vulkan"));
        var selection = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(selection, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(selection, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapters.First(value => value.HardwareAccelerated).Id, [new(QueueType.Compute)]));
        var resources = new List<IDisposable>();
        try
        {
            T Keep<T>(T value) where T : IDisposable { resources.Add(value); return value; }
            var description = TestShaderLoading.CreateSessionDescription(
                backendName == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
            description.SearchPaths =
            [
                Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
                Path.Combine(root, "assets/Shaders"),
            ];
            using var source = new ShaderSourceBlock { SourceNames = ["software_raster_coverage.slang"] };
            var shader = await source.CreateAsync(TestShaderLoading.Slang, description, "Software raster scissor coverage");
            var reflection = shader.GetSlangReflection(0);
            GraphicsPipeline Pipeline(string name)
            {
                var entry = shader.RequireSlangEntry(0, name, SlangStage.Compute);
                return Keep(backend.CreateComputePipeline(device,
                    new(shader.SlangCode, reflection, entry, shader.GetSlangEntryBytes(0, entry),
                        CodeBlob: shader.GetSlangEntryBlob(0, entry))));
            }
            GraphicsPipeline clear = Pipeline("ClearCoverage");
            GraphicsPipeline actual = Pipeline("RasterCoverage" + threadCount);
            GraphicsPipeline expected = Pipeline("RasterCoverageRectangle" + threadCount);
            Buffer constants = Keep(backend.CreateBuffer(device,
                new BufferDesc(256, BufferUsages.Constant), MemoryType.Upload));
            using (MappedBuffer mapping = backend.Map(constants, MapType.Write, BufferRange.Whole))
            {
                mapping.Bytes.Clear();
                MemoryMarshal.Write(mapping.Bytes, in inputs);
                string inputHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(mapping.Bytes[..256])).ToLowerInvariant();
                Console.WriteLine($"Coverage input CBV SHA256={inputHash}");
                mapping.Flush(mapping.Range);
            }
            ulong outputBytes = inputs.ImageWidth * (ulong)inputs.ImageHeight * sizeof(uint) * 2;
            Buffer output = Keep(backend.CreateBuffer(device,
                new BufferDesc(outputBytes, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal));
            Buffer readback = Keep(backend.CreateBuffer(device,
                new BufferDesc(outputBytes, BufferUsages.CopyDestination), MemoryType.Readback));
            ResourceBinding[] bindings =
            [
                ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(constants, BufferRange.Whole)))),
                ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device,
                    new(output, BufferRange.Whole, StructureStride: sizeof(uint))))),
            ];
            using (CommandContext commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
            {
                backend.Begin(commands);
                backend.Barrier(commands, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                    ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                Dispatch(clear, (inputs.ImageWidth * inputs.ImageHeight * 2 + 63) / 64);
                backend.Barrier(commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                Dispatch(actual, 1);
                backend.Barrier(commands, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                Dispatch(expected, 1);
                backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(commands, new(output, 0, readback, 0, outputBytes));
                using RecordedCommands recorded = backend.End(commands);
                QueueCompletion completed = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [recorded], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completed, TimeSpan.FromSeconds(30)));

                void Dispatch(GraphicsPipeline pipeline, uint groups)
                {
                    backend.SetPipeline(commands, pipeline);
                    backend.SetTransientParameterBindings(commands,
                        new(reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, bindings, []));
                    backend.Dispatch(commands, new(groups, 1, 1));
                }
            }
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            return result.Bytes.ToArray();
        }
        finally
        {
            for (int index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
        }
    }
}
