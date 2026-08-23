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
            _ = RequirePipeline(nativeDevice, desc.Pipeline, nameof(desc));
        var layout = new VulkanIndirectCommandLayout(
            nativeDevice,
            type,
            desc.Stride,
            desc.Pipeline,
            desc.Label);
        nativeDevice.RegisterChild(layout);
        return layout;
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
                    Api.CmdDispatchIndirect(command.NativeRecording, arguments.Native, offset + checked((ulong)index * layout.Stride));
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
            default:
                throw new NotSupportedException("Vulkan has no core counted indirect dispatch command.");
        }
    }

    private sealed class VulkanIndirectCommandLayout : IndirectCommandLayout
    {
        private readonly VulkanDevice _device;
        internal VulkanIndirectCommandLayout(
            VulkanDevice device,
            IndirectArgumentType type,
            uint stride,
            Pipeline? pipeline,
            string? label)
            : base(device, stride, pipeline, label)
        {
            _device = device;
            Type = type;
        }

        internal IndirectArgumentType Type { get; }
        internal override void Release(bool fromParent) => _device.UnregisterChild(this);
    }
}
