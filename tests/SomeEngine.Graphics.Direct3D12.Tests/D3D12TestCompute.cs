using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static class D3D12TestCompute
{
    internal static uint[][] Execute(string file, string entry, DispatchArguments dispatch,
        (uint[] Data, ResourceBindingType Type, uint Stride)[] inputs)
    {
        string? directory = null;
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "assets", "Shaders")))
            { directory = Path.Combine(current.FullName, "assets", "Shaders"); break; }
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.CompileWithSearchPaths("compute_test", $"#include \"{file}\"",
            [new(entry, SlangStage.Compute)], "sm_6_0", directory ?? throw new DirectoryNotFoundException());
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        List<IDisposable> owned = [];
        try
        {
            T Keep<T>(T item) where T : IDisposable { owned.Add(item); return item; }
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
            List<ResourceBinding> bindings = [];
            List<(Buffer Buffer, ulong Size)> outputs = [];
            foreach (var input in inputs)
            {
                ulong bytes = (ulong)input.Data.Length * 4u;
                bool constant = input.Type == ResourceBindingType.ConstantBuffer, writable = input.Type == ResourceBindingType.BufferUav;
                if (constant) bytes = (bytes + 255u) & ~255ul;
                Buffer upload = Keep(backend.CreateBuffer(device, new BufferDesc(bytes,
                    writable ? BufferUsages.CopySource : constant ? BufferUsages.Constant : BufferUsages.ShaderRead), MemoryType.Upload));
                using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
                { map.Bytes.Clear(); MemoryMarshal.AsBytes(input.Data.AsSpan()).CopyTo(map.Bytes); map.Flush(new(0, bytes)); }
                Buffer buffer = upload;
                if (writable)
                {
                    buffer = Keep(backend.CreateBuffer(device, new BufferDesc(bytes,
                        BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination), MemoryType.DeviceLocal));
                    backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
                    backend.CopyBuffer(context, new BufferCopy(upload, 0, buffer, 0, bytes));
                    backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
                    outputs.Add((buffer, bytes));
                }
                bindings.Add(constant ? ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(buffer, new(0, bytes))))) :
                    writable ? ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device, new(buffer, BufferRange.Whole, StructureStride: input.Stride)))) :
                    ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(buffer, BufferRange.Whole, StructureStride: input.Stride)))));
            }
            Pipeline pipeline = Keep(backend.CreateComputePipeline(device, new(shader.Program, shader.GetEntryPoint(0))));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? ShaderVariableLayout.Null,
                bindings.ToArray(), []));
            backend.Dispatch(context, dispatch);
            ulong total = outputs.Aggregate(0ul, (sum, buffer) => sum + buffer.Size), offset = 0;
            Buffer readback = Keep(backend.CreateBuffer(device, new(total, BufferUsages.CopyDestination), MemoryType.Readback));
            foreach (var output in outputs)
            {
                backend.Barrier(context, new BufferBarrier(output.Buffer, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new BufferCopy(output.Buffer, 0, readback, offset, output.Size));
                offset += output.Size;
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(new(0, total));
            List<uint[]> values = [];
            int start = 0;
            foreach (var output in outputs)
            { values.Add(MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(start, (int)output.Size)).ToArray()); start += (int)output.Size; }
            return values.ToArray();
        }
        finally { for (int index = owned.Count - 1; index >= 0; index--) owned[index].Dispose(); }
    }
}
