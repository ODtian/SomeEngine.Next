namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private IndirectCommandLayout CreateIndirectCommandLayoutCore(
        RhiDevice device,
        in IndirectCommandLayoutDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (desc.Arguments.Length != 1 || desc.Stride == 0 || (desc.Stride & 3) != 0)
            throw new NotSupportedException("Core Vulkan indirect layouts require one aligned command argument.");
        IndirectArgumentType type = desc.Arguments[0].Type;
        if (type is not (IndirectArgumentType.Draw or IndirectArgumentType.DrawIndexed or IndirectArgumentType.Dispatch))
            throw new NotSupportedException($"Core Vulkan indirect execution does not support {type} layouts.");
        uint minimumStride = type switch
        {
            IndirectArgumentType.Draw => 16,
            IndirectArgumentType.DrawIndexed => 20,
            IndirectArgumentType.Dispatch => 12,
            _ => 0,
        };
        if (desc.Stride < minimumStride)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Pipeline is not null)
        {
            VulkanPipeline pipeline = RequirePipeline(
                nativeDevice,
                desc.Pipeline,
                nameof(desc));
            PipelineType expected = type == IndirectArgumentType.Dispatch
                ? PipelineType.Compute
                : PipelineType.Graphics;
            if (pipeline.Type != expected)
                throw new ArgumentException("The indirect Pipeline has the wrong type.", nameof(desc));
        }
        IndirectCommandsLayoutEXT generated = type == IndirectArgumentType.Dispatch &&
            nativeDevice.ExtendedFeatures.DeviceGeneratedCommands
                ? CreateGeneratedDispatchLayout(nativeDevice, desc.Stride, desc.Pipeline)
                : default;
        VulkanIndirectCommandLayout? layout = null;
        try
        {
            layout = new VulkanIndirectCommandLayout(
                nativeDevice,
                type,
                desc.Stride,
                desc.Pipeline,
                generated,
                desc.Label);
            nativeDevice.RegisterChild(layout);
            return layout;
        }
        catch
        {
            if (layout is not null)
            {
                layout.Dispose();
            }
            else if (generated.Handle != 0)
            {
                nativeDevice.GeneratedCommandsApi.DestroyIndirectCommandsLayout(
                    nativeDevice.Native,
                    generated,
                    null);
            }
            throw;
        }
    }

    private void ExecuteIndirectCore(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (layout is not VulkanIndirectCommandLayout nativeLayout ||
            !ReferenceEquals(nativeLayout.Device, command.Device))
            throw new ArgumentException("The indirect layout belongs to a different Vulkan Device.", nameof(layout));
        nativeLayout.ThrowIfDisposed();
        if (maximumCommandCount == 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCommandCount));
        VulkanBuffer argumentBuffer = RequireBuffer(
            (VulkanDevice)command.Device,
            arguments.Buffer,
            nameof(arguments));
        BufferRange argumentRange = arguments.Range.Resolve(argumentBuffer.Info.Size);
        ulong required = checked((ulong)maximumCommandCount * nativeLayout.Stride);
        if (argumentRange.Size < required || (argumentRange.Offset & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(arguments));
        command.Capture(argumentBuffer);
        command.Capture(nativeLayout);

        if (count is BufferRegion countRegion)
        {
            VulkanBuffer countBuffer = RequireBuffer(
                (VulkanDevice)command.Device,
                countRegion.Buffer,
                nameof(count));
            BufferRange countRange = countRegion.Range.Resolve(countBuffer.Info.Size);
            if (countRange.Size < sizeof(uint) || (countRange.Offset & 3) != 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            command.Capture(countBuffer);
            ExecuteIndirectCount(
                command,
                nativeLayout,
                argumentBuffer,
                argumentRange.Offset,
                countBuffer,
                countRange.Offset,
                maximumCommandCount);
            return;
        }
        ExecuteIndirectFixed(
            command,
            nativeLayout,
            argumentBuffer,
            argumentRange.Offset,
            maximumCommandCount);
    }

    private void ExecuteIndirectFixed(
        VulkanCommandContext command,
        VulkanIndirectCommandLayout layout,
        VulkanBuffer arguments,
        ulong offset,
        uint count)
    {
        switch (layout.Type)
        {
            case IndirectArgumentType.Draw:
                Api.CmdDrawIndirect(command.NativeRecording, arguments.Native, offset, count, layout.Stride);
                break;
            case IndirectArgumentType.DrawIndexed:
                Api.CmdDrawIndexedIndirect(command.NativeRecording, arguments.Native, offset, count, layout.Stride);
                break;
            case IndirectArgumentType.Dispatch:
                for (uint index = 0; index < count; index++)
                    Api.CmdDispatchIndirect(
                        command.NativeRecording,
                        arguments.Native,
                        offset + checked((ulong)index * layout.Stride));
                break;
            default:
                throw new NotSupportedException();
        }
    }

    private void ExecuteIndirectCount(
        VulkanCommandContext command,
        VulkanIndirectCommandLayout layout,
        VulkanBuffer arguments,
        ulong argumentOffset,
        VulkanBuffer count,
        ulong countOffset,
        uint maximumCount)
    {
        switch (layout.Type)
        {
            case IndirectArgumentType.Draw:
                Api.CmdDrawIndirectCount(
                    command.NativeRecording,
                    arguments.Native,
                    argumentOffset,
                    count.Native,
                    countOffset,
                    maximumCount,
                    layout.Stride);
                break;
            case IndirectArgumentType.DrawIndexed:
                Api.CmdDrawIndexedIndirectCount(
                    command.NativeRecording,
                    arguments.Native,
                    argumentOffset,
                    count.Native,
                    countOffset,
                    maximumCount,
                    layout.Stride);
                break;
            case IndirectArgumentType.Dispatch:
                ExecuteGeneratedDispatchCount(
                    command,
                    layout,
                    arguments,
                    argumentOffset,
                    count,
                    countOffset,
                    maximumCount);
                break;
            default:
                throw new NotSupportedException(
                    "The Vulkan indirect argument has no counted execution path.");
        }
    }

    private IndirectCommandsLayoutEXT CreateGeneratedDispatchLayout(
        VulkanDevice device,
        uint stride,
        Pipeline? publicPipeline)
    {
        VkPipelineLayout pipelineLayout = publicPipeline is null
            ? default
            : RequirePipeline(device, publicPipeline, nameof(publicPipeline)).Layout.Native;
        IndirectCommandsLayoutTokenEXT token = new()
        {
            SType = StructureType.IndirectCommandsLayoutTokenExt,
            Type = IndirectCommandsTokenTypeEXT.DispatchExt,
            Offset = 0,
        };
        IndirectCommandsLayoutCreateInfoEXT createInfo = new()
        {
            SType = StructureType.IndirectCommandsLayoutCreateInfoExt,
            ShaderStages = ShaderStageFlags.ComputeBit,
            IndirectStride = stride,
            PipelineLayout = pipelineLayout,
            TokenCount = 1,
            PTokens = &token,
        };
        IndirectCommandsLayoutEXT layout = default;
        device.ThrowIfDeviceCallFailed(
            device.GeneratedCommandsApi.CreateIndirectCommandsLayout(
                device.Native,
                &createInfo,
                null,
                &layout),
            "vkCreateIndirectCommandsLayoutEXT(dispatch)");
        return layout;
    }

    private void ExecuteGeneratedDispatchCount(
        VulkanCommandContext command,
        VulkanIndirectCommandLayout layout,
        VulkanBuffer arguments,
        ulong argumentOffset,
        VulkanBuffer count,
        ulong countOffset,
        uint maximumCount)
    {
        if (layout.Generated.Handle == 0)
        {
            throw new NotSupportedException(
                "Counted Vulkan dispatch requires VK_EXT_device_generated_commands.");
        }
        VulkanPipeline pipeline = command.CurrentPipeline;
        if (pipeline.Type != PipelineType.Compute ||
            layout.Pipeline is not null && !ReferenceEquals(layout.Pipeline, pipeline))
            throw new InvalidOperationException("Counted dispatch requires the layout's Compute Pipeline.");
        VulkanDevice device = (VulkanDevice)command.Device;
        GeneratedCommandsPipelineInfoEXT pipelineInfo = new()
        {
            SType = StructureType.GeneratedCommandsPipelineInfoExt,
            Pipeline = pipeline.Native,
        };
        GeneratedCommandsMemoryRequirementsInfoEXT requirementInfo = new()
        {
            SType = StructureType.GeneratedCommandsMemoryRequirementsInfoExt,
            PNext = &pipelineInfo,
            IndirectCommandsLayout = layout.Generated,
            MaxSequenceCount = maximumCount,
        };
        MemoryRequirements2 requirements = new()
        {
            SType = StructureType.MemoryRequirements2,
        };
        device.GeneratedCommandsApi.GetGeneratedCommandsMemoryRequirements(
            device.Native,
            &requirementInfo,
            &requirements);
        VulkanPreprocessBuffer scratch = VulkanPreprocessBuffer.Create(
            device,
            requirements.MemoryRequirements);
        try
        {
            command.Capture(scratch);
            GeneratedCommandsInfoEXT generated = new()
            {
                SType = StructureType.GeneratedCommandsInfoExt,
                PNext = &pipelineInfo,
                ShaderStages = ShaderStageFlags.ComputeBit,
                IndirectCommandsLayout = layout.Generated,
                IndirectAddress = checked(arguments.DeviceAddress + argumentOffset),
                IndirectAddressSize = checked((ulong)maximumCount * layout.Stride),
                PreprocessAddress = scratch.Address,
                PreprocessSize = scratch.Size,
                MaxSequenceCount = maximumCount,
                SequenceCountAddress = checked(count.DeviceAddress + countOffset),
            };
            device.GeneratedCommandsApi.CmdExecuteGeneratedCommands(
                command.NativeRecording,
                false,
                &generated);
        }
        finally
        {
            scratch.ReleaseNative();
        }
    }

    private sealed class VulkanIndirectCommandLayout : IndirectCommandLayout, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private IndirectCommandsLayoutEXT _generated;
        internal VulkanIndirectCommandLayout(
            VulkanDevice device,
            IndirectArgumentType type,
            uint stride,
            Pipeline? pipeline,
            IndirectCommandsLayoutEXT generated,
            string? label)
            : base(device, stride, pipeline, label)
        {
            _device = device;
            _generated = generated;
            Type = type;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal IndirectArgumentType Type { get; }
        internal IndirectCommandsLayoutEXT Generated => _generated;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent)
        {
            _device.UnregisterChild(this);
            _lifetime.Release();
        }

        private void DestroyNative()
        {
            if (_generated.Handle != 0)
                _device.GeneratedCommandsApi.DestroyIndirectCommandsLayout(
                    _device.Native,
                    _generated,
                    null);
            _generated = default;
        }
    }

    private sealed class VulkanPreprocessBuffer : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanMemoryBlock _memory;
        private readonly VulkanLifetime _lifetime;
        private VkBuffer _buffer;

        private VulkanPreprocessBuffer(
            VulkanDevice device,
            VkBuffer buffer,
            VulkanMemoryBlock memory,
            ulong size,
            ulong address)
        {
            _device = device;
            _buffer = buffer;
            _memory = memory;
            Size = size;
            Address = address;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal ulong Size { get; }
        internal ulong Address { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal static VulkanPreprocessBuffer Create(
            VulkanDevice device,
            in Silk.NET.Vulkan.MemoryRequirements generatedRequirements)
        {
            ulong size = Math.Max(generatedRequirements.Size, 4);
            BufferUsageFlags2CreateInfoKHR usage = new()
            {
                SType = StructureType.BufferUsageFlags2CreateInfoKhr,
                Usage = BufferUsageFlags2.PreprocessBufferBitExt |
                    BufferUsageFlags2.ShaderDeviceAddressBit,
            };
            BufferCreateInfo createInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                PNext = &usage,
                Size = size,
                Usage = BufferUsageFlags.ShaderDeviceAddressBit,
                SharingMode = SharingMode.Exclusive,
            };
            VkBuffer buffer = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateBuffer(device.Native, &createInfo, null, &buffer),
                "vkCreateBuffer(generated-command preprocess)");
            VulkanMemoryBlock? memory = null;
            try
            {
                Silk.NET.Vulkan.MemoryRequirements bufferRequirements;
                device.Backend.Api.GetBufferMemoryRequirements(
                    device.Native,
                    buffer,
                    &bufferRequirements);
                uint memoryTypes = bufferRequirements.MemoryTypeBits &
                    generatedRequirements.MemoryTypeBits;
                memory = device.AllocateMemory(
                    Math.Max(bufferRequirements.Size, generatedRequirements.Size),
                    memoryTypes,
                    MemoryType.DeviceLocal,
                    deviceAddress: true);
                device.ThrowIfDeviceCallFailed(
                    device.Backend.Api.BindBufferMemory(
                        device.Native,
                        buffer,
                        memory.Native,
                        0),
                    "vkBindBufferMemory(generated-command preprocess)");
                BufferDeviceAddressInfo addressInfo = new()
                {
                    SType = StructureType.BufferDeviceAddressInfo,
                    Buffer = buffer,
                };
                ulong address = device.Backend.Api.GetBufferDeviceAddress(
                    device.Native,
                    &addressInfo);
                return new VulkanPreprocessBuffer(
                    device,
                    buffer,
                    memory,
                    size,
                    address);
            }
            catch
            {
                device.Backend.Api.DestroyBuffer(device.Native, buffer, null);
                memory?.Release();
                throw;
            }
        }

        private void DestroyNative()
        {
            if (_buffer.Handle != 0)
                _device.Backend.Api.DestroyBuffer(_device.Native, _buffer, null);
            _buffer = default;
            _memory.Release();
        }
    }
}
