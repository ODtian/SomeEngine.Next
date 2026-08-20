namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorSlotDesc> slots,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default);

    DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint slot);

    void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value);

    PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        Pipeline pipeline,
        in ParameterBlockBindings bindings,
        string? label = null);

    void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings);

    void PublishDescriptors(
        Device device,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default);
}
