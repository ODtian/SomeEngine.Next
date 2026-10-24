using SomeEngine.Graphics;

namespace SomeEngine.Render;

public sealed partial class RenderDevice
{
    private readonly Dictionary<SamplerDesc, (Sampler Sampler, DescriptorTable Table, uint Index)> _indexedSamplers = [];

    /// <summary>Returns the sampler descriptor index carried by user material scalar data.</summary>
    public uint GetSamplerDescriptorIndex(SamplerDesc description)
    {
        ThrowIfDisposed();
        if (_indexedSamplers.TryGetValue(description, out var value)) return value.Index;
        Sampler sampler = _backend.CreateSampler(_device, description);
        DescriptorTable table = _backend.CreateDescriptorTable(_device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)], "Material sampler");
        _backend.WriteDescriptor(table, 0, ResourceBinding.SampledWith(sampler));
        uint index = _backend.GetDescriptorIndex(table, 0).Value;
        _indexedSamplers.Add(description, (sampler, table, index));
        return index;
    }

    private void DisposeSamplers()
    {
        foreach (var value in _indexedSamplers.Values) { value.Table.Dispose(); value.Sampler.Dispose(); }
        _indexedSamplers.Clear();
    }
}
