using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static class D3D12TestCompute
{
    internal sealed record BufferInput(
        string Name,
        uint[] Data,
        ResourceBindingType Type,
        uint Stride);

    internal sealed record SampledImage(uint Width, uint Height, uint[] Pixels, int BindingIndex);
    internal static uint[][] Execute(string file, string entry, DispatchArguments dispatch,
        (uint[] Data, ResourceBindingType Type, uint Stride)[] inputs)
        => ExecuteSource($"#include \"{file}\"", entry, dispatch, inputs);

    internal static uint[][] Execute(
        string file,
        string entry,
        DispatchArguments dispatch,
        BufferInput[] inputs) =>
        ExecuteSourceCore($"#include \"{file}\"", entry, dispatch, inputs, null);

    internal static uint[][] ExecuteSource(string source, string entry, DispatchArguments dispatch,
        (uint[] Data, ResourceBindingType Type, uint Stride)[] inputs, SampledImage? image = null) =>
        ExecuteSourceCore(
            source,
            entry,
            dispatch,
            inputs.Select(static input => new BufferInput("", input.Data, input.Type, input.Stride)).ToArray(),
            image);

    private static uint[][] ExecuteSourceCore(
        string source,
        string entry,
        DispatchArguments dispatch,
        BufferInput[] inputs,
        SampledImage? image)
    {
        string? directory = null;
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "assets", "Shaders")))
            { directory = Path.Combine(current.FullName, "assets", "Shaders"); break; }
        D3D12TestShader shader = D3D12TestShader.CompileWithSearchPaths("compute_test", source,
            [new(entry, SlangStage.Compute)], "sm_6_0", directory ?? throw new DirectoryNotFoundException());
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        List<IDisposable> owned = [];
        try
        {
            T Keep<T>(T item) where T : IDisposable { owned.Add(item); return item; }
            using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
            backend.Begin(context, new CommandRecordingDesc(64, 8, 64));
            List<(string Name, ResourceBinding Binding)> bindings = [];
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
                ResourceBinding binding = constant
                    ? ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(buffer, new(0, bytes)))))
                    : writable
                        ? ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device, new(buffer, BufferRange.Whole, StructureStride: input.Stride))))
                        : ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(buffer, BufferRange.Whole, StructureStride: input.Stride))));
                bindings.Add((input.Name, binding));
            }
            if (image is not null)
            {
                uint pitch = (image.Width * 4 + 255u) & ~255u;
                Buffer upload = Keep(backend.CreateBuffer(device, new((ulong)pitch * image.Height, BufferUsages.CopySource), MemoryType.Upload));
                using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
                {
                    map.Bytes.Clear();
                    for (int row = 0; row < image.Height; row++)
                        MemoryMarshal.AsBytes(image.Pixels.AsSpan(row * (int)image.Width, (int)image.Width))
                            .CopyTo(map.Bytes.Slice(row * (int)pitch));
                    map.Flush(map.Range);
                }
                Texture texture = Keep(backend.CreateTexture(device, new(TextureDimension.Texture2D,
                    image.Width, image.Height, 1, 1, 1, 1, Format.R32UInt, TextureUsages.Sampled | TextureUsages.CopyDestination)));
                TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.Copy,
                    ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
                backend.CopyBufferToTexture(context, new(upload, 0, pitch, image.Height, texture, 0, 0, TextureAspects.Color,
                    0, 0, 0, image.Width, image.Height, 1));
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                    ResourceAccess.CopyDestination, ResourceAccess.ShaderResource, TextureLayout.CopyDestination, TextureLayout.ShaderResource));
                TextureSrv view = Keep(backend.CreateTextureSrv(device, new(texture, range, Format.R32UInt, TextureViewDimension.Texture2D)));
                bindings.Insert(image.BindingIndex, ("", ResourceBinding.SampledTexture(view)));
            }
            Pipeline pipeline = Keep(backend.CreateComputePipeline(device, new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0))));
            backend.SetPipeline(context, pipeline);
            ResourceBinding[] resourceBindings = bindings.Any(static binding => binding.Name.Length != 0)
                ? OrderNamedBindings(shader.Reflection, bindings)
                : bindings.Select(static binding => binding.Binding).ToArray();
            backend.SetTransientParameterBindings(context, new(shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null,
                resourceBindings, []));
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

    private static ResourceBinding[] OrderNamedBindings(
        ShaderReflection reflection,
        IReadOnlyList<(string Name, ResourceBinding Binding)> bindings)
    {
        var byName = new Dictionary<string, Queue<ResourceBinding>>(StringComparer.Ordinal);
        foreach ((string name, ResourceBinding binding) in bindings)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("Named test bindings cannot be mixed with ordinal bindings.");
            if (!byName.TryGetValue(name, out Queue<ResourceBinding>? values))
                byName.Add(name, values = new Queue<ResourceBinding>());
            values.Enqueue(binding);
        }

        VariableLayoutReflection root = reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        if (root == VariableLayoutReflection.Null)
            throw new InvalidDataException("The Slang test entry has no global parameter layout.");
        TypeLayoutReflection contents = root.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();

        var result = new List<ResourceBinding>(bindings.Count);
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            nint descriptorRangeCount = contents.GetBindingRangeDescriptorRangeCount(range);
            if (descriptorRangeCount <= 0)
                continue;
            string name = contents.GetBindingRangeLeafVariable(range).Name;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            for (nint relative = 0; relative < descriptorRangeCount; relative++)
            {
                nint count = contents.GetDescriptorSetDescriptorRangeDescriptorCount(
                    set,
                    first + relative);
                nuint countMarker = unchecked((nuint)count);
                if (count < 0 || countMarker == Slang.UnknownSize || countMarker == Slang.UnboundedSize)
                {
                    throw new InvalidDataException(
                        $"Slang test resource '{name}' has an unresolved descriptor count.");
                }
                for (nint element = 0; element < count; element++)
                {
                    if (!byName.TryGetValue(name, out Queue<ResourceBinding>? values) ||
                        !values.TryDequeue(out ResourceBinding binding))
                    {
                        throw new InvalidDataException(
                            $"The test did not supply Slang resource '{name}'.");
                    }
                    result.Add(binding);
                }
            }
        }
        string[] remaining = byName
            .Where(static pair => pair.Value.Count != 0)
            .Select(static pair => pair.Key)
            .ToArray();
        if (remaining.Length != 0)
        {
            throw new InvalidDataException(
                "The test supplied resources absent from Slang reflection: " +
                string.Join(", ", remaining));
        }
        return [.. result];
    }
}
