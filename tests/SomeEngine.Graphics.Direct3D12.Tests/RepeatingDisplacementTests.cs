using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class RepeatingDisplacementTests
{
    [Fact]
    public void ValuesAndGradientsMatchBilinearRepeatAcrossSizesAndSeams()
    {
        var coordinates = new List<Vector2>();
        for (int y = -8; y < 8; y++)
        for (int x = -16; x < 16; x++) coordinates.Add(new(x / 8f, y / 8f));
        foreach (float offset in new[] { -1f / 65536f, 0f, 1f / 65536f })
        for (int repeat = -3; repeat <= 3; repeat++)
        {
            coordinates.Add(new(repeat + offset, 0.375f));
            coordinates.Add(new(0.625f, repeat + offset));
            coordinates.Add(new(repeat + offset, -repeat - offset));
        }
        string source = $$"""
            #include "texture_displacement.slang"
            Texture2D<float> HeightMap;
            StructuredBuffer<float2> Coordinates;
            RWStructuredBuffer<float4> Results;
            [shader("compute")]
            [numthreads(32, 1, 1)]
            void SampleDisplacement(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= {{coordinates.Count}}u) return;
                TextureDisplacement surface;
                surface.Map = HeightMap;
                surface.Scale = -0.75;
                surface.Bias = 0.125;
                Results[id.x] = float4(surface.sample(Coordinates[id.x]), 1.0);
            }
            """;
        string? shaders = null;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "Shaders")))
            { shaders = Path.Combine(directory.FullName, "assets", "Shaders"); break; }
        D3D12TestShader shader = D3D12TestShader.CompileWithSearchPaths(
            "repeating_displacement", source, [new("SampleDisplacement", SlangStage.Compute)], "sm_6_0",
            shaders ?? throw new DirectoryNotFoundException());
        using IGraphicsBackend backend = new SomeEngine.Graphics.Validation.ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device, new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        using Buffer input = backend.CreateBuffer(device, new((ulong)coordinates.Count * 8, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer map = backend.Map(input, MapType.Write, BufferRange.Whole))
        {
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(coordinates)).CopyTo(map.Bytes);
            map.Flush(map.Range);
        }
        using BufferSrv inputView = backend.CreateBufferSrv(device, new(input, BufferRange.Whole, StructureStride: 8));
        foreach ((int width, int height) in new[] { (1, 1), (1, 7), (7, 1), (3, 5), (8, 8), (17, 31), (65, 3) })
            Check(width, height);

        void Check(int width, int height)
        {
            uint pitch = ((uint)width * 4 + 255u) & ~255u;
            using Buffer upload = backend.CreateBuffer(device, new(pitch * (ulong)height, BufferUsages.CopySource), MemoryType.Upload);
            using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                map.Bytes.Clear();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float value = Height(x, y);
                    MemoryMarshal.Write(map.Bytes.Slice((int)((ulong)y * pitch + (ulong)x * 4), 4), in value);
                }
                map.Flush(map.Range);
            }
            using Texture texture = backend.CreateTexture(device, new(TextureDimension.Texture2D,
                (uint)width, (uint)height, 1, 1, 1, 1, Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
            using TextureSrv textureView = backend.CreateTextureSrv(device, new(texture, range, Format.R32Float, TextureViewDimension.Texture2D));
            ulong bytes = (ulong)coordinates.Count * 16;
            using Buffer output = backend.CreateBuffer(device, new(bytes, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
            using BufferUav outputView = backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 16));
            using Buffer readback = backend.CreateBuffer(device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback);
            using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(16, 4, 16));
            backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
            backend.CopyBufferToTexture(context, new(upload, 0, pitch, (uint)height, texture, 0, 0,
                TextureAspects.Color, 0, 0, 0, (uint)width, (uint)height, 1));
            backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                [ResourceBinding.Null(ResourceBindingType.BufferSrv), ResourceBinding.Null(ResourceBindingType.ConstantBuffer),
                 ResourceBinding.SampledTexture(textureView), ResourceBinding.ReadOnlyBuffer(inputView), ResourceBinding.WritableBuffer(outputView)], []));
            backend.Dispatch(context, new((uint)(coordinates.Count + 31) / 32, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, bytes));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            ReadOnlySpan<Vector4> values = MemoryMarshal.Cast<byte, Vector4>(result.Bytes);
            for (int index = 0; index < coordinates.Count; index++)
            {
                Vector2 uv = coordinates[index];
                double x = (uv.X - Math.Floor(uv.X)) * width - 0.5;
                double y = (uv.Y - Math.Floor(uv.Y)) * height - 0.5;
                int x0 = ((int)Math.Floor(x) % width + width) % width, x1 = (x0 + 1) % width;
                int y0 = ((int)Math.Floor(y) % height + height) % height, y1 = (y0 + 1) % height;
                double fx = x - Math.Floor(x), fy = y - Math.Floor(y);
                double a = Height(x0, y0), b = Height(x1, y0), c = Height(x0, y1), d = Height(x1, y1);
                double value = Mix(Mix(a, b, fx), Mix(c, d, fx), fy) * -0.75 + 0.125;
                double dx = Mix(b - a, d - c, fy) * width * -0.75;
                double dy = Mix(c - a, d - b, fx) * height * -0.75;
                double[] expected = [value, dx, dy, 1.0];
                for (int component = 0; component < 4; component++)
                    Assert.True(Math.Abs(values[index][component] - expected[component]) <= Math.Max(1e-6, Math.Abs(expected[component]) * 2e-6),
                        $"{width}x{height}, uv={uv}, component={component}: {values[index][component]} != {expected[component]}");
            }
        }

        static float Height(int x, int y) => ((x * 5 + y * 3) % 29) / 32f;
        static double Mix(double a, double b, double t) => a + (b - a) * t;
    }
}
