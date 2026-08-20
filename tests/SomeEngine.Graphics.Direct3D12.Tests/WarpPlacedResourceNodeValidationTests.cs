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
        using var validation = new ValidationLayer(new D3D12Backend());
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

    [Fact]
    public void Placed_resource_use_rejects_a_heap_not_visible_to_the_executing_queue_node()
    {
        using var validation = new ValidationLayer(new D3D12Backend());
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
        OverrideVisibleNodeMask(validation, heap, 2);

        validation.Begin(context);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            validation.CopyBuffer(context, new BufferCopy(source, 0, destination, 0, 256)));
        Assert.Contains("VisibleNodeMask 0x2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("queue node mask 0x1", failure.Message, StringComparison.Ordinal);
        validation.Discard(context);
    }

    private static void OverrideVisibleNodeMask(
        ValidationLayer validation,
        Heap heap,
        uint visibleNodeMask)
    {
        Type layerType = validation.GetType();
        FieldInfo field = layerType.GetField(
            "_heapStates",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object states = field.GetValue(validation)!;
        Type stateType = states.GetType().GenericTypeArguments[^1];
        object state = Activator.CreateInstance(
            stateType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [visibleNodeMask],
            culture: null)!;
        const BindingFlags registryFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        _ = states.GetType().GetMethod(
            "Remove",
            registryFlags,
            binder: null,
            types: [typeof(Heap)],
            modifiers: null)!.Invoke(states, [heap]);
        states.GetType().GetMethod(
            "EnsureAdditionalCapacity",
            registryFlags,
            binder: null,
            types: [typeof(int)],
            modifiers: null)!.Invoke(states, [1]);
        states.GetType().GetMethod(
            "Add",
            registryFlags,
            binder: null,
            types: [typeof(Heap), stateType],
            modifiers: null)!.Invoke(states, [heap, state]);
    }
}
