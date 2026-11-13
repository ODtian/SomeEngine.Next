namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorEntryDesc> entries,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default);

    DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint entry);

    void WriteDescriptor(
        DescriptorTable table,
        uint entry,
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
