using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class PublishedDescriptorCopyTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(129)]
    public void Published_indices_keep_samples_and_typed_nulls_after_heap_growth_and_disposal(int count)
    {
        D3D12TestShader shader = CompileSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.TextureSrv,
                Format.R32Float, TextureDimension: TextureViewDimension.Texture2D), count).ToArray());
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.Sampler), count).ToArray());
        uint[] expected = new uint[count * 2];
        for (uint index = 0; index < count; index++)
        {
            ResourceBinding texture = index % 7 == 3
                ? ResourceBinding.Null(ResourceBindingType.TextureSrv)
                : ResourceBinding.SampledTexture(index % 2 == 0 ? firstView : secondView);
            backend.WriteDescriptor(textures, index, texture);
            backend.WriteDescriptor(samplers, index, ResourceBinding.SampledWith(index % 3 == 1 ? border : edge));
            expected[index * 2] = index % 7 == 3 ? 0u : index % 2 == 0 ? 25u : 75u;
            expected[index * 2 + 1] = index % 3 == 1 ? 0u : expected[index * 2];
        }
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[8];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        ulong byteCount = checked((ulong)expected.Length * sizeof(uint));
        using Buffer output = backend.CreateBuffer(device,
            new(byteCount, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(byteCount, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        for (int recording = 0; recording < 2; recording++)
        {
            backend.Begin(context, recording == 0
                ? new CommandRecordingDesc(1, 1, 8)
                : new CommandRecordingDesc(512, 256, 8));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context,
                new(layout, [ResourceBinding.WritableBuffer(outputView)], ordinary));
            backend.Dispatch(context, new(checked((uint)count), 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, byteCount));
            using RecordedCommands commands = backend.End(context);
            if (recording == 1)
            {
                textures.Dispose();
                samplers.Dispose();
                firstView.Dispose();
                secondView.Dispose();
                first.Dispose();
                second.Dispose();
                edge.Dispose();
                border.Dispose();
                backend.PublishDescriptors(device);
            }
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
                new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, byteCount));
            mapped.Invalidate(new(0, byteCount));
            Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            backend.CollectCompleted(device);
        }
    }

    [Fact]
    public void Resource_only_publish_keeps_old_samples_after_pre_cancellation_and_retry()
    {
        D3D12TestShader shader = CompileSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[8];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        using Buffer output = backend.CreateBuffer(device,
            new(8, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        BeginSamples(backend, context, pipeline, layout, output, outputView, ordinary);
        using RecordedCommands beforeUpdate = EndSampleReadback(backend, context, output, readback, 8);

        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(secondView));
        Assert.Throws<OperationCanceledException>(() => backend.PublishDescriptors(
            device, cancellationToken: new CancellationToken(true)));
        BeginSamples(backend, context, pipeline, layout, output, outputView, ordinary);
        using RecordedCommands afterCancellation = EndSampleReadback(backend, context, output, readback, 8);
        Assert.Equal(new uint[] { 25, 25 },
            ReadSamples(backend, device, afterCancellation, readback, 2));

        backend.PublishDescriptors(device);
        BeginSamples(backend, context, pipeline, layout, output, outputView, ordinary);
        using RecordedCommands afterUpdate = EndSampleReadback(backend, context, output, readback, 8);
        Assert.Equal(new uint[] { 25, 25 },
            ReadSamples(backend, device, beforeUpdate, readback, 2));
        Assert.Equal(new uint[] { 75, 75 },
            ReadSamples(backend, device, afterUpdate, readback, 2));
    }

    [Fact]
    public void Sampler_only_publish_keeps_old_samples_after_a_later_resource_update()
    {
        D3D12TestShader shader = CompileSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[8];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        using Buffer output = backend.CreateBuffer(device,
            new(16, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstOutputView = backend.CreateBufferUav(device,
            new(output, new BufferRange(0, 8), StructureStride: sizeof(uint)));
        using BufferUav secondOutputView = backend.CreateBufferUav(device,
            new(output, new BufferRange(8, 8), StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(16, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext edgeContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        using CommandContext borderContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        BeginSamples(backend, edgeContext, pipeline, layout, output, firstOutputView, ordinary);
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(border));
        backend.PublishDescriptors(device);
        BeginSamples(backend, borderContext, pipeline, layout, output, firstOutputView, ordinary);

        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(secondView));
        backend.PublishDescriptors(device);

        // Continue both indexed recordings with another UAV view after the publishes.
        // Their small initial capacities exercise copying the retained resource descriptors.
        backend.Barrier(edgeContext, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        DispatchSamples(backend, edgeContext, pipeline, layout, secondOutputView, ordinary);
        using RecordedCommands edgeCommands = EndSampleReadback(backend, edgeContext, output, readback, 16);
        backend.Barrier(borderContext, new BufferBarrier(output, PipelineSync.ComputeShading,
            PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
        DispatchSamples(backend, borderContext, pipeline, layout, secondOutputView, ordinary);
        using RecordedCommands borderCommands = EndSampleReadback(backend, borderContext, output, readback, 16);

        BeginSamples(backend, borderContext, pipeline, layout, output, firstOutputView, ordinary);
        using RecordedCommands changedTexture = EndSampleReadback(backend, borderContext, output, readback, 8);
        Assert.Equal(new uint[] { 25, 25, 25, 25 },
            ReadSamples(backend, device, edgeCommands, readback, 4));
        Assert.Equal(new uint[] { 25, 0, 25, 0 },
            ReadSamples(backend, device, borderCommands, readback, 4));
        Assert.Equal(new uint[] { 75, 0 },
            ReadSamples(backend, device, changedTexture, readback, 2));
    }

    [Fact]
    public void Resource_only_publishes_keep_indexed_samples_after_sampler_change_and_disposal()
    {
        D3D12TestShader shader = CompileIndexedSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using Buffer readback = backend.CreateBuffer(device,
            new(256, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        backend.PublishDescriptors(device);

        // No sampling commands retain any of these earlier published heaps.
        foreach (TextureSrv view in new[] { secondView, firstView, secondView })
        {
            backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(view));
            backend.PublishDescriptors(device);
        }

        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands beforeSamplerChange = EndIndexedSampleReadback(backend, context, output, readback);

        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(border));
        backend.PublishDescriptors(device);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess,
            TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterSamplerChange = EndIndexedSampleReadback(backend, context, output, readback);

        textures.Dispose();
        samplers.Dispose();
        outputs.Dispose();
        firstView.Dispose();
        secondView.Dispose();
        outputView.Dispose();
        first.Dispose();
        second.Dispose();
        output.Dispose();
        edge.Dispose();
        border.Dispose();
        backend.PublishDescriptors(device);

        Assert.Equal(new uint[] { 75, 75 },
            ReadSamples(backend, device, beforeSamplerChange, readback, 2));
        Assert.Equal(new uint[] { 75, 0 },
            ReadSamples(backend, device, afterSamplerChange, readback, 2));
    }

    [Fact]
    public void Sampler_only_publishes_keep_indexed_samples_after_resource_change_and_disposal()
    {
        D3D12TestShader shader = CompileIndexedSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using Buffer readback = backend.CreateBuffer(device,
            new(256, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        backend.PublishDescriptors(device);

        using DescriptorTable extraSamplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        Assert.Throws<InvalidOperationException>(() => backend.PublishDescriptors(device));
        backend.WriteDescriptor(extraSamplers, 0, ResourceBinding.SampledWith(edge));
        backend.PublishDescriptors(device);

        // No sampling commands retain any of these earlier published heaps.
        foreach (Sampler sampler in new[] { border, edge, border })
        {
            backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
            backend.PublishDescriptors(device);
        }

        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands beforeResourceChange = EndIndexedSampleReadback(backend, context, output, readback);

        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(secondView));
        backend.PublishDescriptors(device);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess,
            TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterResourceChange = EndIndexedSampleReadback(backend, context, output, readback);

        textures.Dispose();
        samplers.Dispose();
        extraSamplers.Dispose();
        outputs.Dispose();
        firstView.Dispose();
        secondView.Dispose();
        outputView.Dispose();
        first.Dispose();
        second.Dispose();
        output.Dispose();
        edge.Dispose();
        border.Dispose();
        backend.PublishDescriptors(device);

        Assert.Equal(new uint[] { 25, 0 },
            ReadSamples(backend, device, beforeResourceChange, readback, 2));
        Assert.Equal(new uint[] { 75, 0 },
            ReadSamples(backend, device, afterResourceChange, readback, 2));
    }

    [Fact]
    public void Unpublished_resource_table_disposal_keeps_samples_through_sampler_publish_and_range_reuse()
    {
        D3D12TestShader shader = CompileIndexedSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using Buffer readback = backend.CreateBuffer(device,
            new(256, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        backend.PublishDescriptors(device);

        // This never-published range extends beyond the initial descriptor tables.
        // Disposing it removes its writes; only the sampler changes at the next publish.
        DescriptorSlotDesc[] spareDescriptions = Enumerable.Repeat(
            new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D), 129).ToArray();
        using DescriptorTable spareTextures = backend.CreateDescriptorTable(device, spareDescriptions);
        DescriptorIndex spareIndex = backend.GetDescriptorIndex(spareTextures, 128);
        spareTextures.Dispose();
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(border));
        backend.PublishDescriptors(device);

        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterSamplerChange = EndIndexedSampleReadback(backend, context, output, readback);
        Assert.Equal(new uint[] { 25, 0 },
            ReadSamples(backend, device, afterSamplerChange, readback, 2));

        using DescriptorTable replacementTextures = backend.CreateDescriptorTable(device, spareDescriptions);
        DescriptorIndex replacementIndex = backend.GetDescriptorIndex(replacementTextures, 128);
        Assert.Equal(spareIndex.Value, replacementIndex.Value);
        Assert.NotEqual(spareIndex, replacementIndex);
        for (uint index = 0; index < replacementTextures.Count; index++)
            backend.WriteDescriptor(replacementTextures, index, ResourceBinding.SampledTexture(secondView));
        backend.PublishDescriptors(device);

        MemoryMarshal.Write(ordinary, replacementIndex.Value);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess,
            TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterRangeReuse = EndIndexedSampleReadback(backend, context, output, readback);
        Assert.Equal(new uint[] { 75, 0 },
            ReadSamples(backend, device, afterRangeReuse, readback, 2));
    }

    [Fact]
    public void Unpublished_sampler_table_disposal_keeps_samples_through_resource_publish_and_range_reuse()
    {
        D3D12TestShader shader = CompileIndexedSampleShader();
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using Buffer readback = backend.CreateBuffer(device,
            new(256, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(firstView));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        backend.PublishDescriptors(device);

        // This never-published range extends beyond the initial sampler table.
        // Disposing it removes every unset sampler; only the texture changes at the next publish.
        DescriptorSlotDesc[] spareDescriptions = Enumerable.Repeat(
            new DescriptorSlotDesc(ResourceBindingType.Sampler), 129).ToArray();
        using DescriptorTable spareSamplers = backend.CreateDescriptorTable(device, spareDescriptions);
        DescriptorIndex spareIndex = backend.GetDescriptorIndex(spareSamplers, 128);
        spareSamplers.Dispose();
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(secondView));
        backend.PublishDescriptors(device);

        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterResourceChange = EndIndexedSampleReadback(backend, context, output, readback);
        Assert.Equal(new uint[] { 75, 75 },
            ReadSamples(backend, device, afterResourceChange, readback, 2));

        using DescriptorTable replacementSamplers = backend.CreateDescriptorTable(device, spareDescriptions);
        DescriptorIndex replacementIndex = backend.GetDescriptorIndex(replacementSamplers, 128);
        Assert.Equal(spareIndex.Value, replacementIndex.Value);
        Assert.NotEqual(spareIndex, replacementIndex);
        for (uint index = 0; index < replacementSamplers.Count; index++)
            backend.WriteDescriptor(replacementSamplers, index, ResourceBinding.SampledWith(border));
        backend.PublishDescriptors(device);

        MemoryMarshal.Write(ordinary.AsSpan(4), replacementIndex.Value);
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopySource, ResourceAccess.UnorderedAccess,
            TextureLayout.CopySource, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
        using RecordedCommands afterRangeReuse = EndIndexedSampleReadback(backend, context, output, readback);
        Assert.Equal(new uint[] { 75, 0 },
            ReadSamples(backend, device, afterRangeReuse, readback, 2));
    }

    [Fact]
    public unsafe void Published_indices_keep_current_sparse_texture_samples_after_unmap_and_heap_disposal()
    {
        D3D12TestShader shader = shaderCompiler.Compile("published_sparse_texture_samples", """
            Texture2D<float4> textures[];
            SamplerState samplers[];
            RWTexture2D<uint> outputs[];
            uint textureIndex;
            uint samplerIndex;
            uint outputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                outputs[outputIndex][uint2(0, 0)] = uint(round(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(0.5), 0).r * 255.0));
                outputs[outputIndex][uint2(1, 0)] = uint(round(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(1.5), 0).r * 255.0));
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SparseResources? sparse));
        Assert.NotNull(sparse);
        Assert.True(sparse.Texture2DSupported);
        Assert.True(sparse.SupportedTexture2DFormats.Contains(Format.R8G8B8A8UNorm));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        using Texture texture = backend.CreateReservedTexture(device,
            new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination));
        SparseResourceInfo info = backend.GetSparseResourceInfo(texture);
        uint tileCount = checked((uint)info.TotalTileCount);
        Assert.True(tileCount > 0);
        ulong heapSize = checked(info.TotalTileCount * sparse.TileSizeInBytes);
        using Heap firstHeap = backend.CreateHeap(device,
            new(heapSize, sparse.TileSizeInBytes, MemoryType.DeviceLocal, HeapFlags.Textures));
        using Heap secondHeap = backend.CreateHeap(device,
            new(heapSize, sparse.TileSizeInBytes, MemoryType.DeviceLocal, HeapFlags.Textures));
        SparseTileRegion tiles = new(new SparseTileCoordinate(0, 0, 0, 0),
            0, 0, 0, tileCount, Boxed: false);
        QueueCompletion firstMapping = backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(texture, tiles, SparseMappingType.Mapped, firstHeap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(firstMapping, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);

        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using Buffer upload = backend.CreateBuffer(device,
            new(256, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Clear();
            mapped.Bytes[0] = 64;
            mapped.Bytes[3] = 255;
            mapped.Flush(new(0, 4));
        }
        using (CommandContext uploadContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
        {
            backend.Begin(uploadContext);
            backend.Barrier(uploadContext, new TextureBarrier(texture, range, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination,
                TextureLayout.Undefined, TextureLayout.CopyDestination));
            backend.CopyBufferToTexture(uploadContext, new(upload, 0, 256, 1, texture, 0, 0,
                TextureAspects.Color, 0, 0, 0, 1, 1, 1));
            backend.Barrier(uploadContext, new TextureBarrier(texture, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            using RecordedCommands uploaded = backend.End(uploadContext);
            QueueCompletion completion = backend.Submit(queue, new([], [], [uploaded], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        using TextureSrv view = backend.CreateTextureSrv(device,
            new(texture, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureSrv, Format.R8G8B8A8UNorm,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using Buffer readback = backend.CreateBuffer(device,
            new(256, BufferUsages.CopyDestination), MemoryType.Readback);
        backend.WriteDescriptor(textures, 0, ResourceBinding.SampledTexture(view));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(edge));
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        backend.PublishDescriptors(device);

        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        using (CommandContext firstContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
        {
            backend.Begin(firstContext);
            backend.Barrier(firstContext, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
                TextureLayout.Undefined, TextureLayout.UnorderedAccess));
            backend.SetPipeline(firstContext, pipeline);
            backend.SetTransientParameterBindings(firstContext, new(layout, [], ordinary));
            backend.Dispatch(firstContext, new(1, 1, 1));
            using RecordedCommands firstSamples = EndIndexedSampleReadback(backend, firstContext, output, readback);
            Assert.Equal(new uint[] { 64, 64 }, ReadSamples(backend, device, firstSamples, readback, 2));
        }
        backend.CollectCompleted(device);

        QueueCompletion secondMapping = backend.UpdateSparseMappings(queue,
            [new SparseMappingDesc(texture, tiles, SparseMappingType.Mapped, secondHeap, 0)]);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(secondMapping, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
        firstHeap.Dispose();
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes[0] = 192;
            mapped.Flush(new(0, 4));
        }
        using (CommandContext uploadContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
        {
            backend.Begin(uploadContext);
            backend.Barrier(uploadContext, new TextureBarrier(texture, range, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.ShaderResource, ResourceAccess.CopyDestination,
                TextureLayout.ShaderResource, TextureLayout.CopyDestination));
            backend.CopyBufferToTexture(uploadContext, new(upload, 0, 256, 1, texture, 0, 0,
                TextureAspects.Color, 0, 0, 0, 1, 1, 1));
            backend.Barrier(uploadContext, new TextureBarrier(texture, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination, TextureLayout.ShaderResource));
            using RecordedCommands uploaded = backend.End(uploadContext);
            QueueCompletion completion = backend.Submit(queue, new([], [], [uploaded], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        using CommandContext secondContext = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        using ExternalTimeline gate = backend.CreateExternalTimeline(device, 0);
        try
        {
            // The descriptor generation is unchanged. Only indexed sampling reaches the sparse texture.
            backend.Begin(secondContext);
            backend.Barrier(secondContext, new TextureBarrier(output, range, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopySource, ResourceAccess.UnorderedAccess,
                TextureLayout.CopySource, TextureLayout.UnorderedAccess));
            backend.SetPipeline(secondContext, pipeline);
            backend.SetTransientParameterBindings(secondContext, new(layout, [], ordinary));
            backend.Dispatch(secondContext, new(1, 1, 1));
            using RecordedCommands secondSamples = EndIndexedSampleReadback(backend, secondContext, output, readback);
            QueueCompletion sampled = backend.Submit(queue, new([], [new(gate, 1)], [secondSamples], [], []));

            // CPU mapping state advances now; native unmapping remains ordered after sampling.
            QueueCompletion unmapped = backend.UpdateSparseMappings(queue,
                [new SparseMappingDesc(texture, tiles, SparseMappingType.Unmapped, null, 0)]);
            secondHeap.Dispose();
            Assert.True(backend.GetNativeTimeline(gate)->Signal(1) >= 0);
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(unmapped, TimeSpan.FromSeconds(10)));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(sampled, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, 8));
            mapped.Invalidate(new(0, 8));
            Assert.Equal(new uint[] { 192, 192 }, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            backend.CollectCompleted(device);
        }
        finally
        {
            _ = backend.GetNativeTimeline(gate)->Signal(1);
        }
    }

    [Fact]
    public void Published_indices_keep_reordered_samples_and_typed_nulls_across_large_tables()
    {
        const int count = 131;
        D3D12TestShader shader = shaderCompiler.Compile("published_reordered_descriptor_samples", """
            Texture2D<float> textures[];
            SamplerState samplers[];
            RWTexture2D<uint> outputs[];
            uint textureBase;
            uint samplerBase;
            uint outputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputs[outputIndex][uint2(0, id.x)] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(0.5), 0) * 100.0);
                outputs[outputIndex][uint2(1, id.x)] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(1.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        using Texture third = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.5f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, third, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv thirdView = backend.CreateTextureSrv(device,
            new(third, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using Texture output = backend.CreateTexture(device,
            new(TextureDimension.Texture2D, 2, count, 1, 1, 1, 1, Format.R32UInt,
                TextureUsages.Storage | TextureUsages.CopySource));
        using TextureUav outputView = backend.CreateTextureUav(device,
            new(output, range, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable outputs = backend.CreateDescriptorTable(device,
            [new DescriptorSlotDesc(ResourceBindingType.TextureUav, Format.R32UInt,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.TextureSrv,
                Format.R32Float, TextureDimension: TextureViewDimension.Texture2D), count).ToArray());
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.Sampler), count).ToArray());
        backend.WriteDescriptor(outputs, 0, ResourceBinding.StorageTexture(outputView));
        TextureSrv[] views = [firstView, secondView, thirdView];
        uint[] values = [25, 50, 75];
        int[] order = [2, 0, 2, 1, 1, 0];
        uint[] expected = new uint[count * 2];
        for (int index = 0; index < count; index++)
        {
            int source = order[index % order.Length];
            backend.WriteDescriptor(textures, checked((uint)index), index == 65
                ? ResourceBinding.Null(ResourceBindingType.TextureSrv)
                : ResourceBinding.SampledTexture(views[source]));
            backend.WriteDescriptor(samplers, checked((uint)index),
                ResourceBinding.SampledWith(index % 3 == 1 ? border : edge));
            expected[index * 2] = index == 65 ? 0u : values[source];
            expected[index * 2 + 1] = index % 3 == 1 ? 0u : expected[index * 2];
        }
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[12];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(8), backend.GetDescriptorIndex(outputs, 0).Value);
        const ulong byteCount = 256 * count;
        using Buffer readback = backend.CreateBuffer(device,
            new(byteCount, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined, TextureLayout.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(layout, [], ordinary));
        backend.Dispatch(context, new(count, 1, 1));
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource,
            TextureLayout.UnorderedAccess, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new(readback, 0, 256, count, output, 0, 0,
            TextureAspects.Color, 0, 0, 0, 2, count, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, byteCount));
        mapped.Invalidate(new(0, byteCount));
        for (int row = 0; row < count; row++)
        {
            Assert.Equal(expected[row * 2], MemoryMarshal.Read<uint>(mapped.Bytes.Slice(row * 256, 4)));
            Assert.Equal(expected[row * 2 + 1], MemoryMarshal.Read<uint>(mapped.Bytes.Slice(row * 256 + 4, 4)));
        }
        backend.CollectCompleted(device);
    }

    private static RecordedCommands EndIndexedSampleReadback(
        IGraphicsBackend backend,
        CommandContext context,
        Texture output,
        Buffer readback)
    {
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        backend.Barrier(context, new TextureBarrier(output, range, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource,
            TextureLayout.UnorderedAccess, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new(readback, 0, 256, 1, output, 0, 0,
            TextureAspects.Color, 0, 0, 0, 2, 1, 1));
        return backend.End(context);
    }

    private static void BeginSamples(
        IGraphicsBackend backend,
        CommandContext context,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        Buffer output,
        BufferUav outputView,
        byte[] ordinary)
    {
        backend.Begin(context, new CommandRecordingDesc(1, 1, 8));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        DispatchSamples(backend, context, pipeline, layout, outputView, ordinary);
    }

    private static void DispatchSamples(
        IGraphicsBackend backend,
        CommandContext context,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        BufferUav outputView,
        byte[] ordinary)
    {
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context,
            new(layout, [ResourceBinding.WritableBuffer(outputView)], ordinary));
        backend.Dispatch(context, new(1, 1, 1));
    }

    private static RecordedCommands EndSampleReadback(
        IGraphicsBackend backend,
        CommandContext context,
        Buffer output,
        Buffer readback,
        ulong byteCount)
    {
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new(output, 0, readback, 0, byteCount));
        return backend.End(context);
    }

    private static uint[] ReadSamples(
        IGraphicsBackend backend,
        Device device,
        RecordedCommands commands,
        Buffer readback,
        int count)
    {
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
            new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        ulong byteCount = checked((ulong)count * sizeof(uint));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, byteCount));
        mapped.Invalidate(new(0, byteCount));
        uint[] values = MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray();
        backend.CollectCompleted(device);
        return values;
    }

    private D3D12TestShader CompileIndexedSampleShader()
    {
        return shaderCompiler.Compile("published_indexed_descriptor_samples", """
            Texture2D<float> textures[];
            SamplerState samplers[];
            RWTexture2D<uint> outputs[];
            uint textureIndex;
            uint samplerIndex;
            uint outputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                outputs[outputIndex][uint2(0, 0)] = uint(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(0.5), 0) * 100.0);
                outputs[outputIndex][uint2(1, 0)] = uint(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(1.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
    }

    private D3D12TestShader CompileSampleShader()
    {
        return shaderCompiler.Compile("published_descriptor_samples", """
            Texture2D<float> textures[];
            SamplerState samplers[];
            RWStructuredBuffer<uint> outputValues;
            uint textureBase;
            uint samplerBase;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x * 2] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(0.5), 0) * 100.0);
                outputValues[id.x * 2 + 1] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(1.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
    }
}
