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
