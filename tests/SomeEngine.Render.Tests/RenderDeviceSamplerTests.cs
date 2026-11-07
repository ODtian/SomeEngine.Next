using SomeEngine.Graphics;

namespace SomeEngine.Render.Tests;

public sealed class RenderDeviceSamplerTests
{
    [Fact]
    public void SamplerDescriptorIndicesRetainTheSharedDeviceResource()
    {
        using var fixture = new RenderTestDevice();
        var description = new SamplerDesc(FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        using var first = fixture.RenderDevice.RetainSampler(description);
        Sampler sampler = first.Resource;
        uint index = fixture.RenderDevice.GetSamplerDescriptorIndex(description);

        first.Dispose();
        using var second = fixture.RenderDevice.RetainSampler(description);

        Assert.Same(sampler, second.Resource);
        Assert.Equal(index, fixture.RenderDevice.GetSamplerDescriptorIndex(description));
    }

    [Fact]
    public void SamplerReferencesShareResourcesOnlyWhileTheyAreRetained()
    {
        using var fixture = new RenderTestDevice();
        RenderDevice renderDevice = fixture.RenderDevice;
        var description = new SamplerDesc(FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge);
        using var first = renderDevice.RetainSampler(description);
        using var second = renderDevice.RetainSampler(description);
        Sampler resource = first.Resource;
        Assert.Same(resource, second.Resource);
        Assert.Same(fixture.Device, resource.Device);

        first.Dispose();
        first.Dispose();
        using var third = renderDevice.RetainSampler(description);
        Assert.Same(resource, third.Resource);
        using var descriptors = fixture.Backend.CreateDescriptorTable(fixture.Device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)], "Shared sampler behavior");
        fixture.Backend.WriteDescriptor(descriptors, 0, ResourceBinding.SampledWith(second.Resource));

        third.Dispose();
        second.Dispose();
        renderDevice.CollectCompleted();
        using var replacement = renderDevice.RetainSampler(description);
        Assert.NotSame(resource, replacement.Resource);
        Assert.Equal(description, replacement.Resource.Description);
        fixture.Backend.WriteDescriptor(descriptors, 0, ResourceBinding.SampledWith(replacement.Resource));
    }
}
