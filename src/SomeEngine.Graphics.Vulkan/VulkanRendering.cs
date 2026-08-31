namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (desc.Width == 0 || desc.Height == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        command.PrepareColorAttachments(desc.Colors.Length, out Span<RenderingAttachmentInfo> colors);
        for (int index = 0; index < colors.Length; index++)
            colors[index] = CreateColorAttachment(command, desc.Colors[index]);
        RenderingAttachmentInfo depth = default;
        RenderingAttachmentInfo stencil = default;
        bool hasDepth = false;
        bool hasStencil = false;
        if (desc.DepthStencil is DepthStencilAttachmentDesc depthStencil)
        {
            (depth, stencil, hasDepth, hasStencil) = CreateDepthStencilAttachment(command, depthStencil);
        }
        fixed (RenderingAttachmentInfo* colorPointer = colors)
        {
            RenderingFragmentShadingRateAttachmentInfoKHR shadingRate = default;
            void* next = null;
            if (command.TryGetShadingRateAttachment(
                    out VkImageView shadingRateView,
                    out Extent2D shadingRateTexelSize))
            {
                shadingRate = new RenderingFragmentShadingRateAttachmentInfoKHR
                {
                    SType = StructureType.RenderingFragmentShadingRateAttachmentInfoKhr,
                    ImageView = shadingRateView,
                    ImageLayout = ImageLayout.FragmentShadingRateAttachmentOptimalKhr,
                    ShadingRateAttachmentTexelSize = shadingRateTexelSize,
                };
                next = &shadingRate;
            }
            RenderingInfo rendering = new()
            {
                SType = StructureType.RenderingInfo,
                PNext = next,
                Flags = ToNative(desc.Options),
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(desc.Width, desc.Height)),
                LayerCount = 1,
                ColorAttachmentCount = checked((uint)colors.Length),
                PColorAttachments = colorPointer,
                PDepthAttachment = hasDepth ? &depth : null,
                PStencilAttachment = hasStencil ? &stencil : null,
            };
            Api.CmdBeginRendering(command.NativeRecording, &rendering);
        }
        command.BeginRenderScope();
    }

    internal void EndRendering(CommandContext context)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        command.EndTransformFeedbackIfActive();
        command.EndRenderScope();
        Api.CmdEndRendering(command.NativeRecording);
    }

    internal void SetPipeline(CommandContext context, Pipeline pipeline)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanPipeline native = RequirePipeline((VulkanDevice)command.Device, pipeline, nameof(pipeline));
        PipelineBindPoint bindPoint = ToBindPoint(native.Type);
        ReadOnlySpan<VulkanBindlessSetBinding> bindings = native.Layout.BindlessBindings;
        VulkanBindlessSnapshot? snapshot = bindings.IsEmpty
            ? null
            : ((VulkanDevice)command.Device).BindlessPublisher.Acquire(bindings);
        try
        {
            Api.CmdBindPipeline(command.NativeRecording, bindPoint, native.Native);
            if (snapshot is not null)
                BindBindlessDescriptors(command, native, bindPoint, snapshot);
            command.SetCurrentPipeline(native);
            command.Capture(native);
            if (snapshot is not null)
                command.Capture(snapshot);
        }
        finally
        {
            snapshot?.ReleaseNative();
        }
    }

    private void BindBindlessDescriptors(
        VulkanCommandContext command,
        VulkanPipeline pipeline,
        PipelineBindPoint bindPoint,
        VulkanBindlessSnapshot snapshot)
    {
        foreach (ref readonly VulkanBoundDescriptorSet binding in snapshot.Sets.AsSpan())
        {
            VkDescriptorSet set = binding.Native;
            Api.CmdBindDescriptorSets(
                command.NativeRecording,
                bindPoint,
                pipeline.Layout.Native,
                binding.Set,
                1,
                &set,
                0,
                null);
        }
    }

    internal void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (bindings is not VulkanPersistentParameterBindings native ||
            !ReferenceEquals(native.Device, command.Device))
            throw new ArgumentException("The bindings belong to a different Vulkan Device.", nameof(bindings));
        VulkanPipeline pipeline = command.CurrentPipeline;
        if (!ReferenceEquals(native.Pipeline, pipeline))
            throw new ArgumentException("The persistent bindings were created for a different Pipeline.", nameof(bindings));
        VulkanDescriptorGeneration generation = native.AcquireGeneration();
        try
        {
            BindDescriptorGeneration(command, pipeline, generation);
            command.Capture(generation);
        }
        finally
        {
            generation.ReleaseNative();
        }
    }

    internal void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanPipeline pipeline = command.CurrentPipeline;
        VulkanBlockLayout block = pipeline.Layout.GetBlock(bindings.Layout);
        BindTransientDescriptorGeneration(command, pipeline, block, bindings);
    }

    internal void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        command.PrepareVertexBufferStorage(
            bindings.Length,
            out Span<VkBuffer> nativeBuffers,
            out Span<ulong> offsets);
        for (int index = 0; index < bindings.Length; index++)
        {
            VulkanBuffer buffer = RequireBuffer(device, bindings[index].Buffer, nameof(bindings));
            BufferRange range = new BufferRange(
                bindings[index].Offset,
                bindings[index].Size).Resolve(buffer.Info.Size);
            nativeBuffers[index] = buffer.Native;
            offsets[index] = range.Offset;
            command.Capture(buffer);
        }
        fixed (VkBuffer* bufferPointer = nativeBuffers)
        fixed (ulong* offsetPointer = offsets)
        {
            Api.CmdBindVertexBuffers(
                command.NativeRecording,
                firstSlot,
                checked((uint)bindings.Length),
                bufferPointer,
                offsetPointer);
        }
    }

    internal void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanBuffer buffer = RequireBuffer((VulkanDevice)command.Device, binding.Buffer, nameof(binding));
        BufferRange range = new BufferRange(binding.Offset, binding.Size).Resolve(buffer.Info.Size);
        command.Capture(buffer);
        Api.CmdBindIndexBuffer(
            command.NativeRecording,
            buffer.Native,
            range.Offset,
            binding.Type == IndexType.UInt16 ? Silk.NET.Vulkan.IndexType.Uint16 : Silk.NET.Vulkan.IndexType.Uint32);
    }

    internal void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        command.EndTransformFeedbackIfActive();
        if (bindings.IsEmpty)
            return;
        if (!device.ExtendedFeatures.TransformFeedback)
            throw new NotSupportedException("VK_EXT_transform_feedback is unavailable.");
        if (firstSlot > device.ExtendedFeatures.MaximumTransformFeedbackBuffers ||
            bindings.Length >
                device.ExtendedFeatures.MaximumTransformFeedbackBuffers - firstSlot)
            throw new ArgumentOutOfRangeException(nameof(firstSlot));
        command.PrepareTransformFeedbackStorage(
            bindings.Length,
            out Span<VkBuffer> buffers,
            out Span<ulong> offsets,
            out Span<ulong> sizes,
            out Span<VkBuffer> counters,
            out Span<ulong> counterOffsets);
        counters.Clear();
        counterOffsets.Clear();
        for (int index = 0; index < bindings.Length; index++)
        {
            VulkanBuffer buffer = RequireBuffer(device, bindings[index].Buffer, nameof(bindings));
            if ((buffer.Info.Usages & BufferUsages.StreamOutput) == 0)
                throw new ArgumentException(
                    "A transform-feedback Buffer requires StreamOutput usage.",
                    nameof(bindings));
            BufferRange range = new BufferRange(bindings[index].Offset, bindings[index].Size)
                .Resolve(buffer.Info.Size);
            buffers[index] = buffer.Native;
            offsets[index] = range.Offset;
            sizes[index] = range.Size;
            command.Capture(buffer);
            if (bindings[index].FilledSizeBuffer is RhiBuffer publicCounter)
            {
                VulkanBuffer counter = RequireBuffer(device, publicCounter, nameof(bindings));
                if ((counter.Info.Usages & BufferUsages.StreamOutput) == 0 ||
                    (bindings[index].FilledSizeOffset & 3) != 0 ||
                    counter.Info.Size < sizeof(uint) ||
                    bindings[index].FilledSizeOffset > counter.Info.Size - sizeof(uint))
                    throw new ArgumentOutOfRangeException(nameof(bindings));
                counters[index] = counter.Native;
                counterOffsets[index] = bindings[index].FilledSizeOffset;
                command.Capture(counter);
            }
        }
        fixed (VkBuffer* bufferPointer = buffers)
        fixed (ulong* offsetPointer = offsets)
        fixed (ulong* sizePointer = sizes)
        fixed (VkBuffer* counterPointer = counters)
        fixed (ulong* counterOffsetPointer = counterOffsets)
        {
            device.TransformFeedbackApi.CmdBindTransformFeedbackBuffers(
                command.NativeRecording,
                firstSlot,
                checked((uint)bindings.Length),
                bufferPointer,
                offsetPointer,
                sizePointer);
            device.TransformFeedbackApi.CmdBeginTransformFeedback(
                command.NativeRecording,
                firstSlot,
                checked((uint)bindings.Length),
                counterPointer,
                counterOffsetPointer);
        }
        command.BeginTransformFeedback(
            firstSlot,
            bindings.Length);
    }

    internal void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Span<Silk.NET.Vulkan.Viewport> native = command.PrepareViewportStorage(viewports.Length);
        for (int index = 0; index < native.Length; index++)
        {
            Viewport value = viewports[index];
            native[index] = new Silk.NET.Vulkan.Viewport(
                value.X,
                value.Y + value.Height,
                value.Width,
                -value.Height,
                value.MinimumDepth,
                value.MaximumDepth);
        }
        fixed (Silk.NET.Vulkan.Viewport* pointer = native)
            Api.CmdSetViewport(command.NativeRecording, 0, checked((uint)native.Length), pointer);
    }

    internal void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Span<Rect2D> native = command.PrepareScissorStorage(scissors.Length);
        for (int index = 0; index < native.Length; index++)
        {
            ScissorRect value = scissors[index];
            if (value.Width < 0 || value.Height < 0)
                throw new ArgumentOutOfRangeException(nameof(scissors));
            native[index] = new Rect2D(
                new Offset2D(value.X, value.Y),
                new Extent2D(checked((uint)value.Width), checked((uint)value.Height)));
        }
        fixed (Rect2D* pointer = native)
            Api.CmdSetScissor(command.NativeRecording, 0, checked((uint)native.Length), pointer);
    }

    internal void SetBlendConstants(CommandContext context, in Vector4 value)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        float* values = stackalloc float[4] { value.X, value.Y, value.Z, value.W };
        Api.CmdSetBlendConstants(command.NativeRecording, values);
    }

    internal void SetStencilReference(CommandContext context, uint value)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdSetStencilReference(command.NativeRecording, StencilFaceFlags.FrontAndBack, value);
    }

    internal void SetDepthBounds(CommandContext context, float minimum, float maximum)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdSetDepthBounds(command.NativeRecording, minimum, maximum);
    }

    internal void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdSetDepthBias(command.NativeRecording, bias, clamp, slopeScaledBias);
    }

    internal void SetPrimitiveTopology(CommandContext context, SomeEngine.Graphics.PrimitiveTopology topology)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        device.ExtendedDynamicStateApi.CmdSetPrimitiveTopology(
            command.NativeRecording,
            ToNative(topology));
    }

    internal void SetStripCut(CommandContext context, StripCut stripCut)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        device.ExtendedDynamicState2Api.CmdSetPrimitiveRestartEnable(
            command.NativeRecording,
            stripCut != StripCut.Disabled);
    }

    internal void SetPredication(
        CommandContext context,
        RhiBuffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        command.EndConditionalRenderingIfActive();
        if (buffer is null)
            return;
        if (!device.ExtendedFeatures.ConditionalRendering)
            throw new NotSupportedException("VK_EXT_conditional_rendering is unavailable.");
        VulkanBuffer native = RequireBuffer(device, buffer, nameof(buffer));
        if ((offset & 3) != 0 || offset > native.Info.Size - Math.Min(native.Info.Size, 4))
            throw new ArgumentOutOfRangeException(nameof(offset));
        command.Capture(native);
        ConditionalRenderingBeginInfoEXT begin = new()
        {
            SType = StructureType.ConditionalRenderingBeginInfoExt,
            Buffer = native.Native,
            Offset = offset,
            Flags = operation == PredicationOperation.EqualZero
                ? ConditionalRenderingFlagsEXT.InvertedBitExt
                : ConditionalRenderingFlagsEXT.None,
        };
        device.ConditionalRenderingApi.CmdBeginConditionalRendering(command.NativeRecording, &begin);
        command.BeginConditionalRendering();
    }

    internal void Draw(CommandContext context, in DrawArguments arguments)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdDraw(
            command.NativeRecording,
            arguments.VertexCount,
            arguments.InstanceCount,
            arguments.FirstVertex,
            arguments.FirstInstance);
    }

    internal void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdDrawIndexed(
            command.NativeRecording,
            arguments.IndexCount,
            arguments.InstanceCount,
            arguments.FirstIndex,
            arguments.VertexOffset,
            arguments.FirstInstance);
    }

    internal void Dispatch(CommandContext context, in DispatchArguments arguments)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        Api.CmdDispatch(command.NativeRecording, arguments.X, arguments.Y, arguments.Z);
    }

    internal void ExecuteBundle(CommandContext context, RecordedBundle bundle) =>
        throw new NotSupportedException("This Vulkan Device does not report secondary command-buffer bundle support.");

    internal void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (DebugUtilsApi is not { } debugUtils)
            return;
        Span<byte> terminated = utf8Label.Length < 256
            ? stackalloc byte[utf8Label.Length + 1]
            : new byte[utf8Label.Length + 1];
        utf8Label.CopyTo(terminated);
        fixed (byte* labelName = terminated)
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = labelName,
            };
            debugUtils.CmdBeginDebugUtilsLabel(command.NativeRecording, &label);
        }
    }

    internal void EndEvent(CommandContext context)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        DebugUtilsApi?.CmdEndDebugUtilsLabel(command.NativeRecording);
    }

    internal void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (DebugUtilsApi is not { } debugUtils)
            return;
        Span<byte> terminated = utf8Label.Length < 256
            ? stackalloc byte[utf8Label.Length + 1]
            : new byte[utf8Label.Length + 1];
        utf8Label.CopyTo(terminated);
        fixed (byte* labelName = terminated)
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = labelName,
            };
            debugUtils.CmdInsertDebugUtilsLabel(command.NativeRecording, &label);
        }
    }

    private void BindDescriptorGeneration(
        VulkanCommandContext command,
        VulkanPipeline pipeline,
        VulkanDescriptorGeneration generation)
    {
        PipelineBindPoint bindPoint = ToBindPoint(pipeline.Type);
        foreach (ref readonly VulkanBoundDescriptorSet binding in generation.Sets.AsSpan())
        {
            VkDescriptorSet set = binding.Native;
            Api.CmdBindDescriptorSets(
                command.NativeRecording,
                bindPoint,
                pipeline.Layout.Native,
                binding.Set,
                1,
                &set,
                0,
                null);
        }
        if (generation.Block.Ordinary is VulkanOrdinaryBinding ordinary && ordinary.PushConstants)
        {
            fixed (byte* data = generation.PushConstants)
            {
                Api.CmdPushConstants(
                    command.NativeRecording,
                    pipeline.Layout.Native,
                    ordinary.Stages,
                    ordinary.PushConstantOffset,
                    ordinary.Size,
                    data);
            }
        }
    }

    private RenderingAttachmentInfo CreateColorAttachment(
        VulkanCommandContext command,
        in ColorAttachmentDesc desc)
    {
        if (desc.View is not VulkanColorAttachmentView view || !ReferenceEquals(view.Device, command.Device))
            throw new ArgumentException("The color attachment belongs to a different Vulkan Device.", nameof(desc));
        command.Capture(view);
        RenderingAttachmentInfo result = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.Native,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = ToNative(desc.Load),
            StoreOp = ToNative(desc.Store),
            ClearValue = new ClearValue
            {
                Color = new ClearColorValue(
                    desc.ClearValue.X,
                    desc.ClearValue.Y,
                    desc.ClearValue.Z,
                    desc.ClearValue.W),
            },
        };
        if (desc.ResolveView is VulkanColorAttachmentView resolve)
        {
            if (!ReferenceEquals(resolve.Device, command.Device))
                throw new ArgumentException("The resolve attachment belongs to a different Vulkan Device.", nameof(desc));
            command.Capture(resolve);
            result.ResolveMode = ToNative(desc.ResolveType);
            result.ResolveImageView = resolve.Native;
            result.ResolveImageLayout = ImageLayout.ColorAttachmentOptimal;
        }
        else if (desc.ResolveView is not null)
        {
            throw new ArgumentException("The resolve attachment is not a Vulkan view.", nameof(desc));
        }
        return result;
    }

    private static (RenderingAttachmentInfo Depth, RenderingAttachmentInfo Stencil, bool HasDepth, bool HasStencil)
        CreateDepthStencilAttachment(
            VulkanCommandContext command,
            in DepthStencilAttachmentDesc desc)
    {
        if (desc.View is not VulkanDepthStencilView view || !ReferenceEquals(view.Device, command.Device))
            throw new ArgumentException("The depth/stencil attachment belongs to a different Vulkan Device.", nameof(desc));
        command.Capture(view);
        bool hasDepth = (view.Description.Range.Aspects & TextureAspects.Depth) != 0;
        bool hasStencil = (view.Description.Range.Aspects & TextureAspects.Stencil) != 0;
        ImageLayout layout = view.Description.ReadOnlyDepth && view.Description.ReadOnlyStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.DepthStencilAttachmentOptimal;
        RenderingAttachmentInfo depth = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.Native,
            ImageLayout = layout,
            LoadOp = ToNative(desc.DepthLoad),
            StoreOp = ToNative(desc.DepthStore),
            ClearValue = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue(desc.ClearDepth, desc.ClearStencil),
            },
        };
        RenderingAttachmentInfo stencil = depth;
        stencil.LoadOp = ToNative(desc.StencilLoad);
        stencil.StoreOp = ToNative(desc.StencilStore);
        return (depth, stencil, hasDepth, hasStencil);
    }

    private static PipelineBindPoint ToBindPoint(PipelineType type) => type switch
    {
        PipelineType.Graphics or PipelineType.Mesh => PipelineBindPoint.Graphics,
        PipelineType.Compute => PipelineBindPoint.Compute,
        PipelineType.RayTracing => PipelineBindPoint.RayTracingKhr,
        _ => throw new NotSupportedException($"Vulkan cannot bind Pipeline type {type} through a core bind point."),
    };

    private static AttachmentLoadOp ToNative(LoadType load) => load switch
    {
        LoadType.Load => AttachmentLoadOp.Load,
        LoadType.Clear => AttachmentLoadOp.Clear,
        LoadType.Discard => AttachmentLoadOp.DontCare,
        _ => throw new ArgumentOutOfRangeException(nameof(load)),
    };

    private static AttachmentStoreOp ToNative(StoreType store) => store switch
    {
        StoreType.Store => AttachmentStoreOp.Store,
        StoreType.Discard => AttachmentStoreOp.DontCare,
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    private static ResolveModeFlags ToNative(ResolveType resolve) => resolve switch
    {
        ResolveType.Average => ResolveModeFlags.AverageBit,
        ResolveType.Minimum => ResolveModeFlags.MinBit,
        ResolveType.Maximum => ResolveModeFlags.MaxBit,
        ResolveType.SampleZero => ResolveModeFlags.SampleZeroBit,
        _ => throw new ArgumentOutOfRangeException(nameof(resolve)),
    };

    private static RenderingFlags ToNative(RenderingOptions options)
    {
        RenderingFlags result = RenderingFlags.None;
        if ((options & RenderingOptions.Suspending) != 0) result |= RenderingFlags.SuspendingBit;
        if ((options & RenderingOptions.Resuming) != 0) result |= RenderingFlags.ResumingBit;
        return result;
    }
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanCommandContext
    {
        private RenderingAttachmentInfo[] _colorAttachments = [];
        private VulkanPipeline? _currentPipeline;
        private bool _rendering;

        internal VulkanPipeline CurrentPipeline => _currentPipeline
            ?? throw new InvalidOperationException("No Vulkan Pipeline is currently bound.");

        internal void SetCurrentPipeline(VulkanPipeline pipeline) => _currentPipeline = pipeline;

        internal void PrepareColorAttachments(
            int count,
            out Span<RenderingAttachmentInfo> attachments)
        {
            if (_colorAttachments.Length < count)
                Array.Resize(ref _colorAttachments, Math.Max(count, Math.Max(_colorAttachments.Length * 2, 8)));
            attachments = _colorAttachments.AsSpan(0, count);
        }

        internal void BeginRenderScope()
        {
            if (_rendering)
                throw new InvalidOperationException("A Vulkan rendering scope is already active.");
            _rendering = true;
        }

        internal void EndRenderScope()
        {
            if (!_rendering)
                throw new InvalidOperationException("No Vulkan rendering scope is active.");
            _rendering = false;
        }

        private uint _transformFeedbackFirst;
        private int _transformFeedbackCount;
        private VkBuffer[] _bufferScratch = [];
        private ulong[] _offsetScratch = [];
        private ulong[] _sizeScratch = [];
        private Silk.NET.Vulkan.Viewport[] _viewportScratch = [];
        private Rect2D[] _scissorScratch = [];
        private VkBuffer[] _transformFeedbackCounters = [];
        private ulong[] _transformFeedbackCounterOffsets = [];
        private bool _transformFeedback;
        private bool _conditionalRendering;

        internal void BeginTransformFeedback(
            uint first,
            int count)
        {
            _transformFeedback = true;
            _transformFeedbackFirst = first;
            _transformFeedbackCount = count;
        }

        internal void PrepareVertexBufferStorage(
            int count,
            out Span<VkBuffer> buffers,
            out Span<ulong> offsets)
        {
            EnsureArray(ref _bufferScratch, count);
            EnsureArray(ref _offsetScratch, count);
            buffers = _bufferScratch.AsSpan(0, count);
            offsets = _offsetScratch.AsSpan(0, count);
        }

        internal void PrepareTransformFeedbackStorage(
            int count,
            out Span<VkBuffer> buffers,
            out Span<ulong> offsets,
            out Span<ulong> sizes,
            out Span<VkBuffer> counters,
            out Span<ulong> counterOffsets)
        {
            EnsureArray(ref _bufferScratch, count);
            EnsureArray(ref _offsetScratch, count);
            EnsureArray(ref _sizeScratch, count);
            EnsureArray(ref _transformFeedbackCounters, count);
            EnsureArray(ref _transformFeedbackCounterOffsets, count);
            buffers = _bufferScratch.AsSpan(0, count);
            offsets = _offsetScratch.AsSpan(0, count);
            sizes = _sizeScratch.AsSpan(0, count);
            counters = _transformFeedbackCounters.AsSpan(0, count);
            counterOffsets = _transformFeedbackCounterOffsets.AsSpan(0, count);
        }

        internal Span<Silk.NET.Vulkan.Viewport> PrepareViewportStorage(int count)
        {
            EnsureArray(ref _viewportScratch, count);
            return _viewportScratch.AsSpan(0, count);
        }

        internal Span<Rect2D> PrepareScissorStorage(int count)
        {
            EnsureArray(ref _scissorScratch, count);
            return _scissorScratch.AsSpan(0, count);
        }

        internal void EndTransformFeedbackIfActive()
        {
            if (!_transformFeedback)
                return;
            fixed (VkBuffer* counterPointer = _transformFeedbackCounters.AsSpan(0, _transformFeedbackCount))
            fixed (ulong* offsetPointer = _transformFeedbackCounterOffsets.AsSpan(0, _transformFeedbackCount))
            {
                ((VulkanDevice)Device).TransformFeedbackApi.CmdEndTransformFeedback(
                    NativeRecording,
                    _transformFeedbackFirst,
                    checked((uint)_transformFeedbackCount),
                    counterPointer,
                    offsetPointer);
            }
            _transformFeedback = false;
            _transformFeedbackCount = 0;
        }

        internal void BeginConditionalRendering() => _conditionalRendering = true;

        internal void EndConditionalRenderingIfActive()
        {
            if (!_conditionalRendering)
                return;
            ((VulkanDevice)Device).ConditionalRenderingApi.CmdEndConditionalRendering(NativeRecording);
            _conditionalRendering = false;
        }

        internal void FinalizeOptionalScopes()
        {
            EndTransformFeedbackIfActive();
            EndConditionalRenderingIfActive();
        }

        internal void ResetRecordingState()
        {
            _currentPipeline = null;
            _rendering = false;
            _transformFeedback = false;
            _transformFeedbackFirst = 0;
            _transformFeedbackCount = 0;
            _conditionalRendering = false;
            _shadingRateAttachment = default;
            _shadingRateTexelSize = default;
        }

        private static void EnsureArray<T>(ref T[] storage, int count)
        {
            if (storage.Length >= count)
                return;
            int capacity = storage.Length == 0 ? 8 : checked(storage.Length * 2);
            Array.Resize(ref storage, Math.Max(capacity, count));
        }
    }
}
