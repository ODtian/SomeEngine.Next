namespace SomeEngine.Graphics.Direct3D12.Tests;

/// <summary>Test helpers for fixed-slot public descriptor tables.</summary>
internal static class DescriptorTableTestSupport
{
    internal static DescriptorTable CreateDescriptorTable(
        this IGraphicsBackend backend,
        Device device,
        ReadOnlySpan<ResourceBindingType> slotTypes,
        string? label = null)
    {
        if (slotTypes.IsEmpty)
            throw new ArgumentException("A descriptor table requires at least one slot.", nameof(slotTypes));
        bool sampler = slotTypes[0] == ResourceBindingType.Sampler;
        DescriptorSlotDesc[] slots = new DescriptorSlotDesc[slotTypes.Length];
        for (int index = 0; index < slotTypes.Length; index++)
        {
            if ((slotTypes[index] == ResourceBindingType.Sampler) != sampler)
            {
                throw new ArgumentException(
                    "A descriptor table cannot mix sampler and resource slots.",
                    nameof(slotTypes));
            }
            slots[index] = ToSlot(slotTypes[index]);
        }
        return backend.CreateDescriptorTable(device, slots, label);
    }

    internal static DescriptorTable CreateSingleDescriptorTable(
        this IGraphicsBackend backend,
        Device device,
        in DescriptorSlotDesc slot,
        in ResourceBinding binding,
        out DescriptorIndex index,
        string? label = null)
    {
        DescriptorTable table = backend.CreateDescriptorTable(device, [slot], label);
        try
        {
            backend.WriteDescriptor(table, 0, binding);
            index = backend.GetDescriptorIndex(table, 0);
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    private static DescriptorSlotDesc ToSlot(ResourceBindingType type) => type switch
    {
        ResourceBindingType.TextureSrv => new DescriptorSlotDesc(
            type,
            Format.R8G8B8A8UNorm,
            TextureDimension: TextureViewDimension.Texture2D),
        ResourceBindingType.TextureUav => new DescriptorSlotDesc(
            type,
            Format.R8G8B8A8UNorm,
            TextureDimension: TextureViewDimension.Texture2D),
        _ => new DescriptorSlotDesc(type),
    };
}
