using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ImportedResourceMetadataTests
{
    [Fact]
    public void Freeze_rejects_overlapping_alias_imports_but_accepts_disjoint_ranges()
    {
        using Device device = new();
        BufferDesc overlappingDesc = new(257, BufferUsage.CopySource);
        ResourceRequirements overlappingRequirements = device.GetBufferRequirements(overlappingDesc, MemoryType.DeviceLocal);
        HeapHandle overlappingHeap = device.CreateHeap(new HeapDesc(
            checked(overlappingRequirements.Size + overlappingRequirements.Alignment),
            MemoryType.DeviceLocal,
            ResourceHeapClass.Buffer));
        BufferHandle first = device.CreatePlacedBuffer(overlappingHeap, 0, overlappingDesc);
        BufferHandle second = device.CreatePlacedBuffer(
            overlappingHeap,
            overlappingRequirements.Alignment,
            overlappingDesc);

        using (RenderGraph graph = new(device))
        {
            GraphBuilder builder = graph.Begin();
            _ = builder.ImportBuffer(first, BufferUse.CopySource, BufferUse.CopySource);
            _ = builder.ImportBuffer(second, BufferUse.CopySource, BufferUse.CopySource);
            InvalidOperationException? error = null;
            try
            {
                _ = graph.Execute(ref builder);
            }
            catch (InvalidOperationException exception)
            {
                error = exception;
            }
            Assert.NotNull(error);
            Assert.Contains("overlap", error!.Message, StringComparison.OrdinalIgnoreCase);
        }

        device.DestroyBuffer(second);
        device.DestroyBuffer(first);
        device.DestroyHeap(overlappingHeap);

        BufferDesc disjointDesc = new(16, BufferUsage.CopySource);
        ResourceRequirements disjointRequirements = device.GetBufferRequirements(disjointDesc, MemoryType.DeviceLocal);
        HeapHandle disjointHeap = device.CreateHeap(new HeapDesc(
            checked(disjointRequirements.Size * 2),
            MemoryType.DeviceLocal,
            ResourceHeapClass.Buffer));
        BufferHandle left = device.CreatePlacedBuffer(disjointHeap, 0, disjointDesc);
        BufferHandle right = device.CreatePlacedBuffer(disjointHeap, disjointRequirements.Size, disjointDesc);
        using (RenderGraph graph = new(device))
        {
            GraphBuilder builder = graph.Begin();
            _ = builder.ImportBuffer(left, BufferUse.CopySource, BufferUse.CopySource);
            _ = builder.ImportBuffer(right, BufferUse.CopySource, BufferUse.CopySource);
            GraphExecution execution = graph.Execute(ref builder);
            Assert.Empty(execution.Completions);
        }

        device.DestroyBuffer(right);
        device.DestroyBuffer(left);
        device.DestroyHeap(disjointHeap);
    }

    [Fact]
    public void Canonical_shape_excludes_allocation_identity_but_includes_memory_type()
    {
        using Device device = new();
        BufferDesc desc = new(64, BufferUsage.CopySource);
        BufferHandle first = device.CreateBuffer(desc, MemoryType.DeviceLocal);
        BufferHandle second = device.CreateBuffer(desc, MemoryType.DeviceLocal);
        BufferHandle upload = device.CreateBuffer(desc, MemoryType.Upload);

        FrozenGraph firstGraph = FreezeSingleImport(device, first);
        FrozenGraph secondGraph = FreezeSingleImport(device, second);
        FrozenGraph uploadGraph = FreezeSingleImport(device, upload);

        Assert.NotEqual(
            device.GetBufferMetadata(first).Allocation.Identity,
            device.GetBufferMetadata(second).Allocation.Identity);
        Assert.True(firstGraph.Canonical.Equals(secondGraph.Canonical));
        Assert.False(firstGraph.Canonical.Equals(uploadGraph.Canonical));
        Assert.False(firstGraph.DetachForCompilation().Resources[0].ImportedBuffer.Metadata.Allocation.Identity.IsValid);

        device.DestroyBuffer(upload);
        device.DestroyBuffer(second);
        device.DestroyBuffer(first);
    }

    private static FrozenGraph FreezeSingleImport(Device device, BufferHandle handle)
    {
        BufferMetadata metadata = device.GetBufferMetadata(handle);
        GraphRecording recording = new();
        _ = recording.AddBuffer(
            metadata.Description,
            new ImportedBuffer(
                handle,
                metadata,
                BufferUse.CopySource,
                BufferUse.CopySource,
                true));
        return recording.Freeze(device);
    }
}
