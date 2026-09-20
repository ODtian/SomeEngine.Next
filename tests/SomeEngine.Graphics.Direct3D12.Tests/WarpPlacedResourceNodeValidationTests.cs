using System.Reflection;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpPlacedResourceNodeValidationTests
{
    [Fact]
    public void Placed_resource_use_accepts_a_heap_visible_to_the_executing_queue_node()
    {
        using var validation = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(validation);
        using Heap heap = validation.CreateHeap(
            device,
            new HeapDesc(131_072, 0, MemoryType.DeviceLocal, HeapFlags.Buffers));
        using Buffer source = validation.CreatePlacedBuffer(
            device,
            heap,
            0,
            new BufferDesc(65_536, BufferUsages.CopySource));
        using Buffer destination = validation.CreatePlacedBuffer(
            device,
            heap,
            65_536,
            new BufferDesc(65_536, BufferUsages.CopyDestination));
        using CommandContext context = validation.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        validation.Begin(context);
        validation.CopyBuffer(context, new BufferCopy(source, 0, destination, 0, 256));
        using RecordedCommands recorded = validation.End(context);
    }


}
