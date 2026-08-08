using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpPipelineCacheTests
{
    private const string EmptyEnvelopeGolden =
        "53455248494330310200000000000000" +
        "F6C3A6894F9A1C668B009087C07D26C55ACCC306C85B7CBCD32AA80275856596";

    private const string UnsupportedSchemaEnvelope =
        "53455248494330316300000000000000" +
        "75C7EFD1EC755944C379F7094CCF431F078846061397E7F903A044F27158CDAA";

    private const string UnknownFamilyEnvelope =
        "53455248494330310200000001000000FE" +
        "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F" +
        "202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F" +
        "04000000DEADBEEF" +
        "5F78C33274E43FA9DE5659265C1D917E25C03722DCB0B8D27DB8D5FEAA813953" +
        "46417B70AE966098723775C7E85719A920DEE347195FF8D8852B4BAFEF55D7FC";

    [Fact]
    public void Canonical_float_encoding_unifies_signed_zero_and_all_NaN_payloads()
    {
        Assert.Equal(
            D3D12Backend.CanonicalizePipelineKeySingle(0f),
            D3D12Backend.CanonicalizePipelineKeySingle(-0f));
        Assert.Equal(0u, D3D12Backend.CanonicalizePipelineKeySingle(-0f));

        float positiveNan = BitConverter.UInt32BitsToSingle(0x7FC0_0001u);
        float negativeNan = BitConverter.UInt32BitsToSingle(0xFFC1_2345u);
        Assert.Equal(
            D3D12Backend.CanonicalizePipelineKeySingle(positiveNan),
            D3D12Backend.CanonicalizePipelineKeySingle(negativeNan));
        Assert.Equal(
            0x7FC0_0000u,
            D3D12Backend.CanonicalizePipelineKeySingle(positiveNan));
    }

    [Fact]
    public void Empty_envelope_matches_the_schema_golden_and_corruption_fails_closed()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(device, default);

        byte[] golden = Convert.FromHexString(EmptyEnvelopeGolden);
        Assert.Equal(golden, ReadCache(backend, cache));

        byte[] corrupt = (byte[])golden.Clone();
        corrupt[^1] ^= 0x80;
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(corrupt)));

        byte[] unsupported = Convert.FromHexString(UnsupportedSchemaEnvelope);
        Assert.Throws<GraphicsException>(() => backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(unsupported)));
    }

    [Fact]
    public void Unknown_well_formed_family_sections_are_preserved_byte_for_byte()
    {
        byte[] envelope = Convert.FromHexString(UnknownFamilyEnvelope);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache cache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(envelope));

        Assert.Equal(envelope, ReadCache(backend, cache));
    }

    [Fact]
    public void Merge_is_order_independent_and_classic_family_entries_survive_a_cross_run_reload()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeA() {}

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeB() {}
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("computeA", SlangStage.Compute),
            new("computeB", SlangStage.Compute),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_compute",
            source,
            entries);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using PipelineCache left = backend.CreatePipelineCache(device, default);
        using PipelineCache right = backend.CreatePipelineCache(device, default);
        using Pipeline leftPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)),
            left);
        using Pipeline rightPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(1)),
            right);

        using PipelineCache leftThenRight = backend.CreatePipelineCache(device, default);
        using PipelineCache rightThenLeft = backend.CreatePipelineCache(device, default);
        backend.MergePipelineCaches(leftThenRight, [left, right]);
        backend.MergePipelineCaches(rightThenLeft, [right, left]);
        byte[] merged = ReadCache(backend, leftThenRight);
        Assert.Equal(merged, ReadCache(backend, rightThenLeft));

        using PipelineCache reloaded = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(merged));
        using Pipeline reloadedA = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)),
            reloaded);
        using Pipeline reloadedB = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(1)),
            reloaded);
        Assert.Equal(merged, ReadCache(backend, reloaded));
    }

    [Fact]
    public void Ray_tracing_and_Work_Graph_families_replay_after_envelope_reload()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.True(backend.TryGetCapability(device, out RayTracing? rayTracing));
        Assert.NotNull(rayTracing);
        const string raySource = """
            [shader("raygeneration")]
            void rayGenerationMain() {}
            """;
        using D3D12TestShaderProgram rayShader = D3D12TestShaderProgram.Compile(
            "rhi_pipeline_cache_ray",
            raySource,
            [new D3D12TestShaderEntry("rayGenerationMain", SlangStage.RayGeneration)]);
        EntryPointReflection[] rayGeneration = [rayShader.GetEntryPoint(0)];
        RayTracingPipelineDesc rayDescription = new(
            rayShader.Program,
            rayGeneration,
            [],
            [],
            [],
            1,
            0,
            8);
        using PipelineCache rayCache = backend.CreatePipelineCache(device, default);
        using Pipeline rayPipeline = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            rayCache);
        byte[] rayEnvelope = ReadCache(backend, rayCache);
        using PipelineCache reloadedRayCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(rayEnvelope));
        using Pipeline replayedRayPipeline = backend.CreateRayTracingPipeline(
            device,
            rayDescription,
            reloadedRayCache);
        Assert.Equal(rayEnvelope, ReadCache(backend, reloadedRayCache));

        Assert.True(backend.TryGetCapability(device, out WorkGraphs? workGraphs));
        Assert.NotNull(workGraphs);
        const string workGraphSource = """
            struct WorkRecord { uint Remaining; };

            [shader("node")]
            [NodeLaunch("broadcasting")]
            [NodeIsProgramEntry]
            [NodeMaxRecursionDepth(1)]
            [NodeDispatchGrid(1, 1, 1)]
            [numthreads(1, 1, 1)]
            void graphMain(DispatchNodeInputRecord<WorkRecord> input) {}
            """;
        using D3D12TestShaderProgram graphShader =
            D3D12TestShaderProgram.CompileHlslPassThrough(
                "rhi_pipeline_cache_work_graph",
                workGraphSource,
                [new D3D12TestShaderEntry("graphMain", SlangStage.Dispatch)]);
        WorkGraphEntryPointLayout[] graphEntries =
        [
            new(graphShader.GetEntryPoint(0), 0, 1),
        ];
        WorkGraphPipelineDesc graphDescription = new(
            graphShader.Program,
            "RhiPipelineCacheGraph",
            graphEntries,
            [],
            1);
        using PipelineCache graphCache = backend.CreatePipelineCache(device, default);
        using Pipeline graphPipeline = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            graphCache);
        byte[] graphEnvelope = ReadCache(backend, graphCache);
        using PipelineCache reloadedGraphCache = backend.CreatePipelineCache(
            device,
            new PipelineCacheDesc(graphEnvelope));
        using Pipeline replayedGraphPipeline = backend.CreateWorkGraphPipeline(
            device,
            graphDescription,
            reloadedGraphCache);
        Assert.Equal(graphEnvelope, ReadCache(backend, reloadedGraphCache));
    }

    private static byte[] ReadCache(IGraphicsBackend backend, PipelineCache cache)
    {
        Assert.False(backend.TryGetPipelineCacheData(cache, [], out int required));
        Assert.True(required > 0);
        byte[] data = new byte[required];
        Assert.True(backend.TryGetPipelineCacheData(cache, data, out int confirmed));
        Assert.Equal(data.Length, confirmed);
        return data;
    }
}
