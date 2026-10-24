using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class ClusterTessellationBinningTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void Material_and_pattern_bins_preserve_every_patch_and_generate_bounded_dice_and_indexed_draws()
    {
        const int materialCount = 19, binCount = materialCount * 8, patchCount = 1031;
        string[] entries = ["ResetTessellationBins", "CountTessellationBins", "ReserveTessellationBins", "ScatterTessellationBins"];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths(
            "tessellation_bins", "#include \"cluster_tessellation_bin.slang\"",
            entries.Select(name => new D3D12TestEntry(name, SlangStage.Compute)).ToArray(), "sm_6_0", ShaderDirectory());
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        var owned = new List<IDisposable>();
        try
        {
            T Keep<T>(T value) where T : IDisposable { owned.Add(value); return value; }
            Buffer Upload(uint[] data, BufferUsages usages, ulong bytes)
            {
                Buffer buffer = Keep(backend.CreateBuffer(device, new BufferDesc(bytes, usages), MemoryType.Upload));
                using MappedBuffer map = backend.Map(buffer, MapType.Write, BufferRange.Whole);
                map.Bytes.Clear();
                MemoryMarshal.AsBytes(data.AsSpan()).CopyTo(map.Bytes);
                map.Flush(new BufferRange(0, bytes));
                return buffer;
            }
            Buffer constants = Upload([binCount, patchCount, 8u, 0u], BufferUsages.Constant, 256);
            BufferCbv cbv = Keep(backend.CreateBufferCbv(device, new BufferCbvDesc(constants, new BufferRange(0, 256))));
            uint[] patches = new uint[patchCount * 5];
            uint[] expectedCounts = new uint[binCount];
            for (int index = 0; index < patchCount; index++)
            {
                uint material = (uint)((index * 7) % materialCount), factor = (uint)((index * 3 / materialCount) % 8 + 1);
                patches[index * 5] = (uint)index | ((material & 3u) << 30);
                patches[index * 5 + 1] = (material >> 2) | ((factor - 1u) << 22) | ((factor - 1u) << 25) | ((factor - 1u) << 28);
                expectedCounts[material * 8 + factor - 1u]++;
            }
            Buffer source = Upload(patches, BufferUsages.ShaderRead, (ulong)patches.Length * 4u);
            BufferSrv sourceView = Keep(backend.CreateBufferSrv(device, new BufferSrvDesc(source, BufferRange.Whole, StructureStride: 20)));
            Buffer counterUpload = Upload([0u, 0u, patchCount, 0u, 0u, 0u, 0u, 0u], BufferUsages.CopySource, 32);
            ulong[] sizes = [binCount * 4u, binCount * 4u, binCount * 4u, 4u, binCount * 12u, binCount * 20u + materialCount * 12u, 12u, patchCount * 4u, 32u];
            uint[] strides = [4, 4, 4, 4, 0, 0, 0, 4, 0];
            Buffer[] buffers = new Buffer[sizes.Length];
            BufferUav[] views = new BufferUav[sizes.Length];
            for (int index = 0; index < buffers.Length; index++)
            {
                buffers[index] = Keep(backend.CreateBuffer(device, new BufferDesc(sizes[index],
                    BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination), MemoryType.DeviceLocal));
                views[index] = Keep(backend.CreateBufferUav(device, new BufferUavDesc(buffers[index], BufferRange.Whole, StructureStride: strides[index])));
            }
            BufferSrv counters = Keep(backend.CreateBufferSrv(device, new BufferSrvDesc(buffers[8], BufferRange.Whole)));
            ResourceBinding[] bindings = [ResourceBinding.ConstantBuffer(cbv), ResourceBinding.ReadOnlyBuffer(counters),
                ResourceBinding.ReadOnlyBuffer(sourceView), .. views.Select(ResourceBinding.WritableBuffer)];
            ulong bytesTotal = sizes.Aggregate(0ul, (sum, size) => sum + size);
            Buffer readback = Keep(backend.CreateBuffer(device, new BufferDesc(bytesTotal, BufferUsages.CopyDestination), MemoryType.Readback));
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(128, 16, 128));
            for (int index = 0; index < buffers.Length; index++)
                backend.Barrier(context, new BufferBarrier(buffers[index], PipelineSync.None,
                    index == 8 ? PipelineSync.Copy : PipelineSync.ComputeShading, ResourceAccess.NoAccess,
                    index == 8 ? ResourceAccess.CopyDestination : ResourceAccess.UnorderedAccess));
            backend.CopyBuffer(context, new BufferCopy(counterUpload, 0, buffers[8], 0, 32));
            backend.Barrier(context, new BufferBarrier(buffers[8], PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource | ResourceAccess.UnorderedAccess));
            for (int stage = 0; stage < entries.Length; stage++)
            {
                Pipeline pipeline = Keep(backend.CreateComputePipeline(device, new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(stage), shader.GetEntryCode(stage))));
                backend.SetPipeline(context, pipeline);
                backend.SetTransientParameterBindings(context, new ParameterBlockBindings(
                    shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, bindings, []));
                uint groups = stage == 0 ? (binCount + 63u) / 64u : stage == 2 ? (binCount + 127u) / 128u : (patchCount + 63u) / 64u;
                backend.Dispatch(context, new DispatchArguments(groups, 1, 1));
                backend.Barrier(context, new MemoryBarrier(PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess | ResourceAccess.ShaderResource));
            }
            ulong offset = 0;
            for (int index = 0; index < buffers.Length; index++)
            {
                backend.Barrier(context, new BufferBarrier(buffers[index], PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess | ResourceAccess.ShaderResource, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new BufferCopy(buffers[index], 0, readback, offset, sizes[index]));
                offset += sizes[index];
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(new BufferRange(0, bytesTotal));
            uint[][] outputs = new uint[sizes.Length][];
            int byteOffset = 0;
            for (int index = 0; index < sizes.Length; index++)
            {
                outputs[index] = MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(byteOffset, (int)sizes[index])).ToArray();
                byteOffset += (int)sizes[index];
            }
            Assert.Equal(expectedCounts, outputs[0]);
            Assert.Equal(Enumerable.Range(0, patchCount).Select(value => (uint)value), outputs[7].Order());
            for (int bin = 0; bin < binCount; bin++)
            {
                uint factor = (uint)(bin % 8 + 1), start = outputs[1][bin], count = expectedCounts[bin];
                Assert.True(start + count <= patchCount);
                foreach (uint index in outputs[7].AsSpan((int)start, (int)count))
                {
                    uint material = (index * 7) % materialCount, actualFactor = (index * 3 / materialCount) % 8 + 1;
                    Assert.Equal((uint)bin, material * 8 + actualFactor - 1);
                }
                uint firstIndex = (uint)Enumerable.Range(1, (int)factor - 1).Sum(value => value * value * 3);
                Assert.Equal(new uint[] { 3 * factor * factor, count, firstIndex, 0, start }, outputs[5].AsSpan(bin * 5, 5).ToArray());
            }
            for (int material = 0; material < materialCount; material++)
            {
                uint expectedGroups = 0;
                for (int factor = 1; factor <= 8; factor++)
                {
                    uint patchesPerGroup = factor == 1 ? 32u : factor == 2 ? 8u : factor <= 4 ? 2u : 1u;
                    expectedGroups += (expectedCounts[material * 8 + factor - 1] + patchesPerGroup - 1) / patchesPerGroup;
                }
                Assert.Equal(new uint[] { expectedGroups, 1, 1 }, outputs[4].AsSpan(material * 3, 3).ToArray());
                uint meshGroups = 0;
                for (int factor = 0; factor < 8; factor++) meshGroups += expectedCounts[material * 8 + factor];
                Assert.Equal(new uint[] { meshGroups, 1, 1 }, outputs[5].AsSpan(binCount * 5 + material * 3, 3).ToArray());
            }
            Assert.Equal(0u, outputs[8][3]);
        }
        finally { for (int index = owned.Count - 1; index >= 0; index--) owned[index].Dispose(); }
    }

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
