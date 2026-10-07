using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal sealed class D3D12TestCompute(D3D12TestCompiler shaderCompiler)
{
    internal sealed record BufferInput(
        string Name,
        uint[] Data,
        ResourceBindingType Type,
        uint Stride);

    internal sealed record SampledImage(uint Width, uint Height, uint[] Pixels, int BindingIndex,
        Format PixelFormat = Format.R32UInt, TextureViewDimension ViewDimension = TextureViewDimension.Texture2D,
        ResourceBindingType Type = ResourceBindingType.TextureSrv, uint Layers = 1, string Name = "");

    internal uint[][] ExecuteImages(string file, string entry, DispatchArguments dispatch,
        BufferInput[] inputs, SampledImage[] images) =>
        ExecuteSourceCore($"#include \"{file}\"", entry, dispatch, inputs, null, images);
    internal uint[][] Execute(string file, string entry, DispatchArguments dispatch,
        (uint[] Data, ResourceBindingType Type, uint Stride)[] inputs)
        => ExecuteSource($"#include \"{file}\"", entry, dispatch, inputs);

    internal uint[][] Execute(
        string file,
        string entry,
        DispatchArguments dispatch,
        BufferInput[] inputs) =>
        ExecuteSourceCore($"#include \"{file}\"", entry, dispatch, inputs, null);

    internal uint[][] ExecuteSource(string source, string entry, DispatchArguments dispatch,
        (uint[] Data, ResourceBindingType Type, uint Stride)[] inputs, SampledImage? image = null,
        SampledImage[]? images = null) =>
        ExecuteSourceCore(
            source,
            entry,
            dispatch,
            inputs.Select(static input => new BufferInput("", input.Data, input.Type, input.Stride)).ToArray(),
            image,
            images);

    private uint[][] ExecuteSourceCore(
        string source,
        string entry,
        DispatchArguments dispatch,
        BufferInput[] inputs,
        SampledImage? image,
        SampledImage[]? images = null)
    {
        string? directory = null;
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "assets", "Shaders")))
            { directory = Path.Combine(current.FullName, "assets", "Shaders"); break; }
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("compute_test", source,
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
            List<(Texture Texture, SampledImage Image, uint Pitch)> imageOutputs = [];
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
            IEnumerable<SampledImage> sourceImages = image is null ? images ?? [] : images is null ? [image] : [image, .. images];
            foreach (SampledImage sourceImage in sourceImages.OrderBy(value => value.BindingIndex))
            {
                uint texelBytes = sourceImage.PixelFormat == Format.R8UNorm ? 1u : 4u;
                uint pitch = (sourceImage.Width * texelBytes + 255u) & ~255u;
                Buffer upload = Keep(backend.CreateBuffer(device, new((ulong)pitch * sourceImage.Height * sourceImage.Layers, BufferUsages.CopySource), MemoryType.Upload));
                using (MappedBuffer map = backend.Map(upload, MapType.Write, BufferRange.Whole))
                {
                    map.Bytes.Clear();
                    for (int row = 0; row < sourceImage.Height * sourceImage.Layers; row++)
                    {
                        ReadOnlySpan<uint> pixels = sourceImage.Pixels.AsSpan(row * (int)sourceImage.Width, (int)sourceImage.Width);
                        Span<byte> destination = map.Bytes.Slice(row * (int)pitch);
                        if (texelBytes == 1u)
                            for (int column = 0; column < pixels.Length; column++) destination[column] = (byte)pixels[column];
                        else MemoryMarshal.AsBytes(pixels).CopyTo(destination);
                    }
                    map.Flush(map.Range);
                }
                bool writable = sourceImage.Type == ResourceBindingType.TextureUav;
                Texture texture = Keep(backend.CreateTexture(device, new(TextureDimension.Texture2D,
                    sourceImage.Width, sourceImage.Height, 1, 1, sourceImage.Layers, 1, sourceImage.PixelFormat,
                    TextureUsages.Sampled | TextureUsages.CopyDestination |
                        (writable ? TextureUsages.CopySource | TextureUsages.Storage : TextureUsages.None))));
                TextureSubresourceRange range = new(0, 1, 0, sourceImage.Layers, TextureAspects.Color);
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.Copy,
                    ResourceAccess.NoAccess, ResourceAccess.CopyDestination, TextureLayout.Undefined, TextureLayout.CopyDestination));
                for (uint layer = 0; layer < sourceImage.Layers; layer++)
                    backend.CopyBufferToTexture(context, new(upload, (ulong)pitch * sourceImage.Height * layer, pitch, sourceImage.Height,
                        texture, 0, layer, TextureAspects.Color, 0, 0, 0, sourceImage.Width, sourceImage.Height, 1));
                backend.Barrier(context, new TextureBarrier(texture, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                    ResourceAccess.CopyDestination, writable ? ResourceAccess.UnorderedAccess : ResourceAccess.ShaderResource,
                    TextureLayout.CopyDestination, writable ? TextureLayout.UnorderedAccess : TextureLayout.ShaderResource));
                ResourceBinding binding = writable
                    ? ResourceBinding.StorageTexture(Keep(backend.CreateTextureUav(device, new(texture, range, sourceImage.PixelFormat, sourceImage.ViewDimension))))
                    : ResourceBinding.SampledTexture(Keep(backend.CreateTextureSrv(device, new(texture, range, sourceImage.PixelFormat, sourceImage.ViewDimension))));
                bindings.Insert(sourceImage.BindingIndex, (sourceImage.Name, binding));
                if (writable) imageOutputs.Add((texture, sourceImage, pitch));
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
            ulong imageOffset = (total + 511u) & ~511ul;
            ulong imageBytes = imageOutputs.Aggregate(0ul, (sum, output) =>
                sum + (ulong)output.Pitch * output.Image.Height * output.Image.Layers);
            Buffer readback = Keep(backend.CreateBuffer(device, new(imageOutputs.Count == 0 ? total : imageOffset + imageBytes,
                BufferUsages.CopyDestination), MemoryType.Readback));
            foreach (var output in outputs)
            {
                backend.Barrier(context, new BufferBarrier(output.Buffer, PipelineSync.ComputeShading, PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new BufferCopy(output.Buffer, 0, readback, offset, output.Size));
                offset += output.Size;
            }
            ulong textureOffset = imageOffset;
            foreach (var output in imageOutputs)
            {
                var range = new TextureSubresourceRange(0, 1, 0, output.Image.Layers, TextureAspects.Color);
                backend.Barrier(context, new TextureBarrier(output.Texture, range, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource, TextureLayout.UnorderedAccess, TextureLayout.CopySource));
                for (uint layer = 0; layer < output.Image.Layers; layer++)
                    backend.CopyTextureToBuffer(context, new(readback, textureOffset + (ulong)output.Pitch * output.Image.Height * layer,
                        output.Pitch, output.Image.Height, output.Texture, 0, layer, TextureAspects.Color,
                        0, 0, 0, output.Image.Width, output.Image.Height, 1));
                textureOffset += (ulong)output.Pitch * output.Image.Height * output.Image.Layers;
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(new(0, readback.Info.Size));
            List<uint[]> values = [];
            int start = 0;
            foreach (var output in outputs)
            { values.Add(MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(start, (int)output.Size)).ToArray()); start += (int)output.Size; }
            textureOffset = imageOffset;
            foreach (var output in imageOutputs)
            {
                uint[] pixels = new uint[output.Image.Width * output.Image.Height * output.Image.Layers];
                for (int row = 0; row < output.Image.Height * output.Image.Layers; row++)
                {
                    int startByte = checked((int)textureOffset + row * (int)output.Pitch);
                    Span<uint> destination = pixels.AsSpan(row * (int)output.Image.Width, (int)output.Image.Width);
                    if (output.Image.PixelFormat == Format.R8UNorm)
                        for (int column = 0; column < destination.Length; column++) destination[column] = result.Bytes[startByte + column];
                    else MemoryMarshal.Cast<byte, uint>(result.Bytes.Slice(startByte, checked((int)output.Image.Width * 4))).CopyTo(destination);
                }
                values.Add(pixels);
                textureOffset += (ulong)output.Pitch * output.Image.Height * output.Image.Layers;
            }
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
