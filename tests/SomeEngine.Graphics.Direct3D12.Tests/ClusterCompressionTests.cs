using System.Buffers.Binary;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class ClusterCompressionTests
{
    [Theory]
    [InlineData("sm_6_0")]
    [InlineData("sm_6_5")]
    public void Wave_matching_preserves_full_keys_and_divergent_active_lanes(string profile)
    {
        const int count = 512, words = 6;
        const string source = """
            #include "wave_aggregation.slang"
            RWByteAddressBuffer Results;
            uint Key(uint index)
            {
                uint mode = (index / 64u) % 4u;
                return mode == 0u ? 0xFFFFFFFFu : mode == 1u ? index % 3u :
                    mode == 2u ? index % 64u : (index % 7u) * 0x12345678u;
            }
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void TestWaveMatching(uint3 id : SV_DispatchThreadID)
            {
                uint4 mask = 0u.xxxx;
                if (id.x % 5u != 0u)
                    mask = WaveMatchingLanes(Key(id.x));
                Results.Store4(id.x * 24u, mask);
                Results.Store2(id.x * 24u + 16u, uint2(WaveGetLaneCount(), WaveGetLaneIndex()));
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "wave_matching", source, [new("TestWaveMatching", SlangStage.Compute)], profile, ShaderDirectory());
        uint[] result = Execute(shader, count, words);
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<uint> record = result.AsSpan(index * words, words);
            uint[] expected = new uint[4];
            int first = index - checked((int)record[5]);
            for (int lane = 0; index % 5 != 0 && lane < record[4]; lane++)
            {
                int other = first + lane;
                if (other < count && other / 64 == index / 64 && other % 5 != 0 && Key(other) == Key(index))
                    expected[lane / 32] |= 1u << (lane % 32);
            }
            Assert.True(record[..4].SequenceEqual(expected));
        }
        static uint Key(int index) => (index / 64 % 4) switch
        {
            0 => uint.MaxValue,
            1 => (uint)(index % 3),
            2 => (uint)(index % 64),
            _ => unchecked((uint)(index % 7) * 0x12345678u),
        };
    }

    [Fact]
    public void Quadratic_error_bound_contains_the_triangle_and_converges_to_its_boundary_error()
    {
        const int count = 512;
        const string source = """
            #include "triangle_surface.slang"
            #include "phong_tessellation.slang"
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(64,1,1)]
            void TestQuadraticBounds(uint3 id : SV_DispatchThreadID)
            {
                float3 d = float3(id.x % 8u, (id.x / 8u) % 8u, id.x / 64u) / 7.0;
                float bound = BoundQuadraticTriangle(d);
                float maximum = 0.0;
                for (uint y = 0u; y <= 64u; y++)
                for (uint z = 0u; z <= 64u - y; z++)
                {
                    float u = float(y) / 64.0, v = float(z) / 64.0, w = 1.0 - u - v;
                    maximum = max(maximum, 2.0 * (d.x*u*v + d.y*v*w + d.z*w*u));
                }
                Results.Store2(id.x * 8u, asuint(float2(bound, maximum)));
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "quadratic_error", source, [new("TestQuadraticBounds", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        uint[] results = Execute(shader, count, 2);
        for (int index = 0; index < count; index++)
        {
            double a = (index % 8) / 7.0, b = ((index / 8) % 8) / 7.0, c = (index / 64) / 7.0;
            double expected = Math.Max(a, Math.Max(b, c)) * 0.5;
            if (a < b+c && b < a+c && c < a+b)
                expected = Math.Max(expected, 2*a*b*c / (2*(a*b+b*c+c*a)-a*a-b*b-c*c));
            float bound = BitConverter.UInt32BitsToSingle(results[index * 2]);
            float sampled = BitConverter.UInt32BitsToSingle(results[index * 2 + 1]);
            Assert.True(bound >= sampled - 1e-6f);
            Assert.InRange(bound, expected - 1e-6, expected + 1.1e-5);
        }
        // With one remaining outer-edge coefficient, further centroid subdivision
        // must approach the edge error rather than retaining the old full coefficient.
        Assert.InRange(BitConverter.UInt32BitsToSingle(results[7 * 2]), 0.5f, 0.50001f);
    }

    [Fact]
    public void Upload_mapping_can_be_retained_across_submissions_and_copies_share_disposal()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer upload = backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource), MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopyDestination), MemoryType.Readback);
        MappedBuffer[] mappings = [backend.Map(upload, MapType.Write, BufferRange.Whole)];
        try
        {
            for (uint frame = 0; frame < 3; frame++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(mappings[0].Bytes, 1000u + frame);
                mappings[0].Flush(new BufferRange(0, 16));
                GC.Collect();
                using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Copy, 0, 1));
                backend.Begin(context);
                backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, 16));
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Copy),
                    new QueueSubmitDesc([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
                result.Invalidate(new BufferRange(0, 16));
                Assert.Equal(1000u + frame, BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes));
            }
            MappedBuffer copy = mappings[0];
            copy.Dispose();
            Assert.Throws<InvalidOperationException>(() => mappings[0].Bytes.Clear());
        }
        finally
        {
            mappings[0].Dispose();
        }
    }

    [Fact]
    public void Tessellation_records_preserve_indices_factors_and_barycentrics_on_the_gpu()
    {
        const int count = 512 * 6 * 6 * 4;
        const int words = 10;
        const string source = """
            #include "cluster_tessellation_types.slang"
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void TestCompression(uint3 id : SV_DispatchThreadID)
            {
                uint n = id.x;
                if (n >= 73728u) return;
                uint indices[6] = { 0u, 1u, 1048575u, 1048576u, 4194239u, 4194303u };
                uint bins[6] = { 0u, 1u, 3u, 4u, 4194304u, 16777215u };
                uint triangles[4] = { 0u, 1u, 123u, 255u };
                uint factors = n % 512u;
                uint bin = bins[(n / 512u) % 6u];
                TessellationPatch patch;
                patch.VisibleClusterIndex = indices[(n / 3072u) % 6u];
                patch.TriangleAndRasterBin = triangles[n / 18432u] | (bin << 8u);
                patch.Barycentric0 = 65504u;
                patch.Barycentric1 = 65504u << 16u;
                patch.Barycentric2 = 32752u | (32752u << 16u);
                SetTessellationPatchState(patch, 5u,
                    uint3(factors % 8u + 1u, (factors / 8u) % 8u + 1u, factors / 64u + 1u));
                uint4 work = EncodeTessellationWorkPatch(patch);
                TessellationFinalPatch finalPatch = EncodeTessellationFinalPatch(patch);
                TessellationPatch decoded = DecodeTessellationFinalPatch(finalPatch);
                TessellationPatch decodedWork = DecodeTessellationWorkPatch(work, bin, 5u);
                uint address = n * 40u;
                Results.Store4(address, work);
                Results.Store4(address + 16u, uint4(finalPatch.SourceAndBin, finalPatch.BinAndFactors,
                    finalPatch.Barycentric0, finalPatch.Barycentric1));
                Results.Store(address + 32u, finalPatch.Barycentric2);
                bool valid = decoded.VisibleClusterIndex == patch.VisibleClusterIndex &&
                    decoded.TriangleAndRasterBin == patch.TriangleAndRasterBin &&
                    decoded.PackedState == (patch.PackedState & 0xFFFu) &&
                    decoded.Barycentric0 == patch.Barycentric0 && decoded.Barycentric1 == patch.Barycentric1 &&
                    decoded.Barycentric2 == patch.Barycentric2 &&
                    decodedWork.VisibleClusterIndex == patch.VisibleClusterIndex &&
                    decodedWork.TriangleAndRasterBin == patch.TriangleAndRasterBin &&
                    TessellationPatchDepth(decodedWork) == 5u &&
                    decodedWork.Barycentric0 == patch.Barycentric0 && decodedWork.Barycentric1 == patch.Barycentric1 &&
                    decodedWork.Barycentric2 == patch.Barycentric2;
                Results.Store(address + 36u, valid ? 0u : 1u);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "cluster_compression", source, [new("TestCompression", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        uint[] result = Execute(shader, count, words);
        uint[] indices = [0u, 1u, 1048575u, 1048576u, 4194239u, 4194303u];
        uint[] bins = [0u, 1u, 3u, 4u, 4194304u, 16777215u];
        uint[] triangles = [0u, 1u, 123u, 255u];
        for (int index = 0; index < count; index++)
        {
            uint visible = indices[index / 3072 % 6];
            uint bin = bins[index / 512 % 6];
            uint triangle = triangles[index / 18432];
            uint factors = checked((uint)(index % 512));
            ReadOnlySpan<uint> record = result.AsSpan(index * words, words);
            Assert.Equal(visible | (triangle << 22), record[0]);
            Assert.Equal(65504u, record[1]);
            Assert.Equal(65504u << 16, record[2]);
            Assert.Equal(32752u | (32752u << 16), record[3]);
            Assert.Equal(visible | (triangle << 22) | ((bin & 3u) << 30), record[4]);
            Assert.Equal((bin >> 2) | (factors << 22), record[5]);
            Assert.Equal(record[1], record[6]);
            Assert.Equal(record[2], record[7]);
            Assert.Equal(record[3], record[8]);
            Assert.Equal(0u, record[9]);
        }
    }

    [Fact]
    public void Dice_registers_preserve_all_factors_and_partial_patch_groups()
    {
        const string source = """
            #include "cluster_tessellation_types.slang"
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(64,1,1)]
            [WaveSize(32)]
            void TestDiceRegisters(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
            {
                uint factor = group.x % 8u + 1u;
                uint lanes = TessellationDicingLanes(factor);
                uint patchLane = lane % lanes;
                uint patch = lane / lanes;
                uint firstLane = WaveGetLaneIndex() - patchLane;
                uint vertices = TessellationGridVertexCount(factor);
                uint banks = (vertices + lanes - 1u) / lanes;
                // Distinct patches and banks expose cross-patch and divergent-shuffle errors.
                float4 a = float4(patch * 100u + patchLane, 1.0, 2.0, 3.0);
                float4 b = a + float4(lanes, 0.0, 0.0, 0.0);
                float4 c = b + float4(lanes, 0.0, 0.0, 0.0);
                uint errors = 0u;
                for (uint baseTriangle = 0u; baseTriangle < factor * factor; baseTriangle += lanes)
                {
                    bool active = patch != 1u && baseTriangle + patchLane < factor * factor;
                    uint3 indices = active ? TessellationGridTriangleVertexIndices(factor, baseTriangle + patchLane) : 0u.xxx;
                    for (uint corner = 0u; corner < 3u; corner++)
                    {
                        float4 value = ReadTessellationDiceVertex(indices[corner], lanes, firstLane, banks, a, b, c);
                        if (active && any(value != float4(patch * 100u + indices[corner], 1.0, 2.0, 3.0))) errors++;
                    }
                }
                Results.Store((group.x * 64u + lane) * 4u, errors);
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "dice_registers", source, [new("TestDiceRegisters", SlangStage.Compute)], "sm_6_6", ShaderDirectory());
        Assert.Equal(0u, Execute(shader, 512, 1, hardware: true).Aggregate(0u, (sum, errors) => sum + errors));
    }

    [Fact]
    public void Shade_tiles_and_quads_cover_each_material_pixel_once()
    {
        const string source = """
            #include "shade_bin_common.slang"
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(64,1,1)]
            void TestShadeTiles(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
            {
                uint mode = group.x;
                uint bin = mode == 0u ? 7u : mode == 1u ? 0xFFFFFFFFu :
                    mode == 2u ? lane % 3u : mode == 3u ? (lane % 8u) / 2u :
                    mode == 4u ? (lane >= 40u || lane % 8u >= 5u ? 0xFFFFFFFFu : 2u) :
                    (lane * 17u + mode * 13u) % (mode + 1u);
                uint mask; bool full;
                bool selected = ClassifyShadeTile(bin, lane, mask, full);
                Results.Store4((group.x * 64u + lane) * 16u,
                    uint4(bin, selected ? 1u : 0u, full ? 1u : 0u, mask));
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "shade_tiles", source, [new("TestShadeTiles", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        uint[] result = Execute(shader, 16 * 64, 4);
        for (int tile = 0; tile < 16; tile++)
        {
            int[] visits = new int[64];
            for (int lane = 0; lane < 64; lane++)
            {
                int record = (tile * 64 + lane) * 4;
                if (result[record + 1] == 0) continue;
                uint bin = result[record];
                Assert.NotEqual(uint.MaxValue, bin);
                if (result[record + 2] != 0)
                {
                    Assert.Equal(0, lane);
                    for (int pixel = 0; pixel < 64; pixel++)
                    {
                        Assert.Equal(bin, result[(tile * 64 + pixel) * 4]);
                        visits[pixel]++;
                    }
                    continue;
                }
                int quadBase = lane & ~9;
                for (int corner = 0; corner < 4; corner++)
                {
                    if ((result[record + 3] & (1u << corner)) == 0) continue;
                    int pixel = quadBase + (corner & 1) + (corner >> 1) * 8;
                    Assert.Equal(bin, result[(tile * 64 + pixel) * 4]);
                    visits[pixel]++;
                }
            }
            for (int pixel = 0; pixel < 64; pixel++)
                Assert.Equal(result[(tile * 64 + pixel) * 4] == uint.MaxValue ? 0 : 1, visits[pixel]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mesh_compaction_preserves_uint8_indices_and_partial_batches(bool hardware)
    {
        const string source = """
            #include "cluster_mesh_topology.slang"
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(32,1,1)]
            void TestMeshCompaction(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
            {
                uint3 triangle = (group.x * 17u + lane * 7u + uint3(0u, 83u, 166u)) & 255u;
                ClusterMeshVertexMask used = BuildClusterMeshVertexMask(triangle, lane <= group.x, lane);
                Results.Store4((group.x * 32u + lane) * 16u, uint4(
                    ClusterMeshVertexRank(used, triangle.x), ClusterMeshVertexRank(used, triangle.y),
                    ClusterMeshVertexRank(used, triangle.z), ClusterMeshVertexCount(used)));
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "mesh_compaction", hardware ? source.Replace("[numthreads", "[WaveSize(32)] [numthreads", StringComparison.Ordinal) : source,
            [new("TestMeshCompaction", SlangStage.Compute)], hardware ? "sm_6_6" : "sm_6_0", ShaderDirectory());
        uint[] result = Execute(shader, 32 * 32, 4, hardware, groupWidth: 32);
        for (int group = 0; group < 32; group++)
        {
            int[] vertices = Enumerable.Range(0, group + 1)
                .SelectMany(lane => Enumerable.Range(0, 3).Select(corner => (group * 17 + lane * 7 + corner * 83) & 255))
                .Distinct().Order().ToArray();
            Assert.InRange(vertices.Length, 1, 96);
            for (int lane = 0; lane < 32; lane++)
            {
                int offset = (group * 32 + lane) * 4;
                Assert.Equal((uint)vertices.Length, result[offset + 3]);
                if (lane > group) continue;
                for (int corner = 0; corner < 3; corner++)
                {
                    int index = (group * 17 + lane * 7 + corner * 83) & 255;
                    Assert.Equal((uint)Array.BinarySearch(vertices, index), result[offset + corner]);
                }
            }
        }
    }

    private static Device CreateHardwareDevice(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo adapter = adapters.First(static item => item.HardwareAccelerated);
        return backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            label: "Wave32 register-cache integration device"));
    }

    private static uint[] Execute(D3D12TestShaderProgram shader, int count, int words, bool hardware = false, int groupWidth = 64)
    {
        using var backend = new D3D12Backend();
        using Device device = hardware ? CreateHardwareDevice(backend) : D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        ulong size = checked((ulong)count * (ulong)words * sizeof(uint));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(size, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
        using BufferUav outputView = backend.CreateBufferUav(device, new BufferUavDesc(output, BufferRange.Whole));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(size, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
            shader.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null,
            [ResourceBinding.WritableBuffer(outputView)], []));
        backend.Dispatch(context, new DispatchArguments(checked((uint)((count + groupWidth - 1) / groupWidth)), 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, size));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
        using MappedBuffer mapping = backend.Map(readback, MapType.Read, new BufferRange(0, size));
        mapping.Invalidate(new BufferRange(0, size));
        var result = new uint[count * words];
        for (int index = 0; index < result.Length; index++)
            result[index] = BinaryPrimitives.ReadUInt32LittleEndian(mapping.Bytes.Slice(index * sizeof(uint), sizeof(uint)));
        return result;
    }

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "assets", "Shaders");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException("Could not find the Cluster shader sources.");
    }
}
