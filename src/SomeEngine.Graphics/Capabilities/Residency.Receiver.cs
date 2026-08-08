using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    ResidencyInfo GetResidencyInfo(Device device);
    ResidencyResource GetResidencyResource(Heap heap);
    ResidencyResource GetResidencyResource(Resource resource);
    ResidencyResource GetResidencyResource(QueryPool pool);
    ResidencyResource GetResidencyResource(DescriptorTable table);
    QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources);
    void Evict(Device device, ReadOnlySpan<ResidencyResource> resources);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResidencyInfo GetResidencyInfo(Device device) => Receiver.GetResidencyInfo(device);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResidencyResource GetResidencyResource(Heap heap) =>
        Receiver.GetResidencyResource(heap);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResidencyResource GetResidencyResource(Resource resource) =>
        Receiver.GetResidencyResource(resource);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResidencyResource GetResidencyResource(QueryPool pool) =>
        Receiver.GetResidencyResource(pool);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ResidencyResource GetResidencyResource(DescriptorTable table) =>
        Receiver.GetResidencyResource(table);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources) =>
        Receiver.EnqueueMakeResident(queue, resources);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Evict(Device device, ReadOnlySpan<ResidencyResource> resources) =>
        Receiver.Evict(device, resources);
}
