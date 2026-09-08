using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class VirtualShadowRegionTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(64)]
    public void Shared_views_reconstruct_fine_and_coarse_pages_after_negative_clipmap_scroll(int dimension)
    {
        const int viewCount = 2;
        int pagesPerView = dimension * dimension, finePages = viewCount * pagesPerView;
        int pageCount = 2 * finePages, wordsPerGrid = pagesPerView + dimension * ((dimension + 31) / 32);
        int gridWords = 2 * viewCount * wordsPerGrid;
        const string source = """
            #include "vsm_view_compact.slang"
            #define RasterTargetInvocations UnusedRasterRegions
            #include "cluster_raster_region_access.slang"
            #undef RasterTargetInvocations
            RWByteAddressBuffer Results;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void Reconstruct(uint3 id : SV_DispatchThreadID)
            {
                if (id.x >= ActivePhysicalPageCount[0]) return;
                ClusterRasterTargetInvocation region = RasterTargetInvocations[id.x];
                ClusterViewUniforms view = ClusterApplyRasterRegion(
                    RasterInvocationViews[region.SourceViewIndex], region, id.x);
                for (uint row = 0; row < 4; row++)
                {
                    Results.Store4(id.x * 128u + row * 16u, asuint(view.ViewProj[row]));
                    Results.Store4(id.x * 128u + 64u + row * 16u, asuint(view.PrevViewProj[row]));
                }
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths(
            "shadow_regions", source, [new("CompactVirtualShadowViews", SlangStage.Compute), new("Reconstruct", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var owned = new List<IDisposable>();
        try
        {
            T Keep<T>(T value) where T : IDisposable { owned.Add(value); return value; }
            Buffer Upload(uint[] words, BufferUsages usages, ulong bytes)
            {
                Buffer buffer = Keep(backend.CreateBuffer(device, new BufferDesc(bytes, usages), MemoryType.Upload));
                using MappedBuffer map = backend.Map(buffer, MapType.Write, BufferRange.Whole);
                map.Bytes.Clear();
                MemoryMarshal.AsBytes(words.AsSpan()).CopyTo(map.Bytes);
                map.Flush(new BufferRange(0, bytes));
                return buffer;
            }
            BufferSrv Read(uint[] words, uint stride)
            {
                Buffer buffer = Upload(words, BufferUsages.ShaderRead, (ulong)words.Length * 4u);
                return Keep(backend.CreateBufferSrv(device, new BufferSrvDesc(buffer, BufferRange.Whole, StructureStride: stride)));
            }
            uint[] views = new uint[viewCount * 42], previous = new uint[viewCount * 42];
            uint[] clusterViews = new uint[viewCount * 108], active = new uint[pageCount * 2], table = new uint[pageCount * 4];
            Matrix4x4[] currentMatrices = new Matrix4x4[viewCount], previousMatrices = new Matrix4x4[viewCount];
            int[] originX = [-3, -17], originY = [5, -9];
            for (int view = 0; view < viewCount; view++)
            {
                currentMatrices[view] = Matrix4x4.CreateTranslation(view + 0.25f, -0.5f, -3) *
                    Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 1, 0.1f, 100);
                previousMatrices[view] = Matrix4x4.CreateTranslation(view - 0.25f, 0.5f, -3) *
                    Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 1, 0.1f, 100);
                views[view * 42 + 16] = (uint)dimension * 128u;
                views[view * 42 + 17] = 128;
                views[view * 42 + 21] = unchecked((uint)originX[view]);
                views[view * 42 + 23] = (uint)(view * pagesPerView);
                views[view * 42 + 25] = unchecked((uint)originY[view]);
                views[view * 42 + 34] = 1;
                views[view * 42 + 35] = (uint)finePages + (uint)(view * pagesPerView);
                views.AsSpan(view * 42, 42).CopyTo(previous.AsSpan(view * 42));
                previous[view * 42 + 21] = unchecked((uint)(originX[view] - 1));
                previous[view * 42 + 25] = unchecked((uint)(originY[view] + 2));
                MemoryMarshal.Write(MemoryMarshal.AsBytes(clusterViews.AsSpan(view * 108)), in currentMatrices[view]);
                MemoryMarshal.Write(MemoryMarshal.AsBytes(clusterViews.AsSpan(view * 108 + 28)), in previousMatrices[view]);
            }
            for (int index = 0; index < pageCount; index++)
            {
                bool coarse = index >= finePages;
                int view = (index % finePages) / pagesPerView, local = index % pagesPerView, scale = coarse ? 2 : 1;
                int x = FloorDiv(originX[view], scale) + local % dimension;
                int y = FloorDiv(originY[view], scale) + local / dimension;
                int tableIndex = (coarse ? finePages : 0) + view * pagesPerView + (y & (dimension - 1)) * dimension + (x & (dimension - 1));
                active[index * 2] = (uint)index;
                active[index * 2 + 1] = (uint)tableIndex;
                table[tableIndex * 4] = index % 3 == 0 ? 0u : (uint)index + 1u;
                table[tableIndex * 4 + 1] = unchecked((uint)x);
                table[tableIndex * 4 + 2] = unchecked((uint)y);
            }
            Buffer constants = Upload([viewCount, (uint)pageCount, 128, 7, 16, 17, 1, 0], BufferUsages.Constant, 256);
            BufferCbv cbv = Keep(backend.CreateBufferCbv(device, new BufferCbvDesc(constants, new BufferRange(0, 256))));
            ResourceBinding[] reads = [ResourceBinding.ConstantBuffer(cbv), ResourceBinding.ReadOnlyBuffer(Read(active, 8)),
                ResourceBinding.ReadOnlyBuffer(Read([(uint)pageCount], 4)), ResourceBinding.ReadOnlyBuffer(Read(views, 168)),
                ResourceBinding.ReadOnlyBuffer(Read(previous, 168)), ResourceBinding.ReadOnlyBuffer(Read(table, 16)),
                ResourceBinding.ReadOnlyBuffer(Read(clusterViews, 432))];
            ulong[] sizes = [4u * 432u, (uint)pageCount * 48u, (uint)gridWords * 4u, 16u, 7u * 16u, (uint)pageCount * 128u];
            uint[] strides = [432, 48, 4, 0, 0, 0];
            Buffer[] buffers = new Buffer[sizes.Length];
            BufferUav[] uavs = new BufferUav[sizes.Length];
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index] = Keep(backend.CreateBuffer(device, new BufferDesc(sizes[index],
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination), MemoryType.DeviceLocal));
                uavs[index] = Keep(backend.CreateBufferUav(device, new BufferUavDesc(buffers[index], BufferRange.Whole, StructureStride: strides[index])));
            }
            Buffer zeroGrid = Upload(new uint[gridWords], BufferUsages.CopySource, (uint)gridWords * 4u);
            ResourceBinding[] bindings = [.. reads, .. uavs.Take(5).Select(ResourceBinding.WritableBuffer),
                ResourceBinding.Null(ResourceBindingType.BufferSrv), ResourceBinding.WritableBuffer(uavs[5])];
            ulong total = sizes.Aggregate(0ul, (sum, value) => sum + value);
            Buffer readback = Keep(backend.CreateBuffer(device, new BufferDesc(total, BufferUsages.CopyDestination), MemoryType.Readback));
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
            for (int index = 0; index < buffers.Length; index++)
                backend.Barrier(context, new BufferBarrier(buffers[index], PipelineSync.None,
                    index == 2 ? PipelineSync.Copy : PipelineSync.ComputeShading, ResourceAccess.NoAccess,
                    index == 2 ? ResourceAccess.CopyDestination : ResourceAccess.UnorderedAccess));
            backend.CopyBuffer(context, new BufferCopy(zeroGrid, 0, buffers[2], 0, sizes[2]));
            backend.Barrier(context, new BufferBarrier(buffers[2], PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
            for (int entry = 0; entry < 2; entry++)
            {
                Pipeline pipeline = Keep(backend.CreateComputePipeline(device, new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(entry))));
                backend.SetPipeline(context, pipeline);
                backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                    shader.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null, bindings, []));
                backend.Dispatch(context, new DispatchArguments((uint)(pageCount / 64), 1, 1));
                backend.Barrier(context, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
            }
            ulong offset = 0;
            for (int index = 0; index < buffers.Length; index++)
            {
                backend.Barrier(context, new BufferBarrier(buffers[index], PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new BufferCopy(buffers[index], 0, readback, offset, sizes[index]));
                offset += sizes[index];
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(new BufferRange(0, total));
            int regionOffset = (int)sizes[0], gridOffset = regionOffset + (int)sizes[1];
            ReadOnlySpan<uint> regions = MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(regionOffset, (int)sizes[1]));
            ReadOnlySpan<uint> grid = MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(gridOffset, (int)sizes[2]));
            int matrixOffset = (int)(total - sizes[^1]);
            for (int index = 0; index < pageCount; index++)
            {
                bool coarse = index >= finePages;
                int view = (index % finePages) / pagesPerView, local = index % pagesPerView, scale = coarse ? 2 : 1;
                int x = FloorDiv(originX[view], scale) + local % dimension;
                int y = FloorDiv(originY[view], scale) + local / dimension;
                int sourceView = view + (coarse ? viewCount : 0);
                Assert.Equal((uint)sourceView, regions[index * 12 + 6]);
                Assert.Equal(index % 3 == 0 ? uint.MaxValue : (uint)index, regions[index * 12 + 5]);
                Assert.Equal((uint)index + 1, grid[sourceView * wordsPerGrid + local]);
                Assert.Equal(dimension >= 32 ? uint.MaxValue : (1u << dimension) - 1u,
                    grid[sourceView * wordsPerGrid + pagesPerView + local / dimension * ((dimension + 31) / 32) + (local % dimension) / 32]);
                for (int previousIndex = 0; previousIndex < 2; previousIndex++)
                {
                    Matrix4x4 matrix = previousIndex == 0 ? currentMatrices[view] : previousMatrices[view];
                    float ox = originX[view] + (previousIndex == 0 ? 0 : -1);
                    float oy = originY[view] + (previousIndex == 0 ? 0 : 2);
                    Matrix4x4 crop = Matrix4x4.Identity;
                    crop.M11 = crop.M22 = (float)dimension / scale;
                    crop.M41 = crop.M11 - 1 + 2 * ox / scale - 2 * x;
                    crop.M42 = 1 - crop.M22 - 2 * oy / scale + 2 * y;
                    Matrix4x4 expected = matrix * crop;
                    ReadOnlySpan<float> expectedWords = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(in expected, 1));
                    ReadOnlySpan<float> actual = MemoryMarshal.Cast<byte, float>(result.Bytes.Slice(matrixOffset + index * 128 + previousIndex * 64, 64));
                    for (int word = 0; word < 16; word++)
                    {
                        // Shared crops regroup two float matrix products. Allow four
                        // relative float roundoffs rather than a scale-dependent absolute cap.
                        float tolerance = MathF.Max(2e-5f, MathF.Abs(expectedWords[word]) * 4 * 1.1920929e-7f);
                        Assert.InRange(MathF.Abs(expectedWords[word] - actual[word]), 0, tolerance);
                    }
                }
            }
            ReadOnlySpan<uint> dispatch = MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(gridOffset + (int)sizes[2], 16));
            Assert.Equal(new uint[] { 17, 1, 1, 4 }, dispatch.ToArray());
        }
        finally { for (int index = owned.Count - 1; index >= 0; index--) owned[index].Dispose(); }
    }

    private static int FloorDiv(int value, int divisor) => (int)Math.Floor((double)value / divisor);

    private static string ShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "assets", "Shaders");
            if (Directory.Exists(path)) return path;
        }
        throw new DirectoryNotFoundException();
    }
}
