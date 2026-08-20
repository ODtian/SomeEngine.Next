using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpLinkedAdapterMetadataTests
{
    [Fact]
    public void Single_node_objects_publish_resolved_node_provenance()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                65_536,
                BufferUsages.CopySource,
                NodePlacement: new ResourceNodePlacement(1, 1)),
            MemoryType.DeviceLocal);
        using DescriptorTable table = backend.CreateDescriptorTable(
            device,
            [new DescriptorSlotDesc(ResourceBindingType.BufferSrv, Format.R32UInt)],
            nodeIndex: 0);
        using QueryPool queries = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(
                QueryType.Timestamp,
                QueueType.Graphics,
                1,
                NodeIndex: 0));

        Assert.Equal(0u, queue.NodeIndex);
        Assert.Equal(1u, buffer.Info.CreationNodeMask);
        Assert.Equal(1u, buffer.Info.VisibleNodeMask);
        Assert.Equal(0u, table.NodeIndex);
        Assert.Equal(0u, queries.Description.NodeIndex);
    }

    [Fact]
    public void Invalid_resource_node_masks_fail_before_native_creation()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateBuffer(
                device,
                new BufferDesc(
                    65_536,
                    BufferUsages.CopySource,
                    NodePlacement: new ResourceNodePlacement(3, 3)),
                MemoryType.DeviceLocal));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateBuffer(
                device,
                new BufferDesc(
                    65_536,
                    BufferUsages.CopySource,
                    NodePlacement: new ResourceNodePlacement(1, 2)),
                MemoryType.DeviceLocal));
    }

    [Fact]
    public void Invalid_descriptor_table_and_query_nodes_fail_before_native_creation()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateDescriptorTable(
                device,
                [new DescriptorSlotDesc(ResourceBindingType.BufferSrv, Format.R32UInt)],
                nodeIndex: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            backend.CreateQueryPool(
                device,
                new QueryPoolDesc(
                    QueryType.Timestamp,
                    QueueType.Graphics,
                    1,
                    NodeIndex: 1)));
    }
}
