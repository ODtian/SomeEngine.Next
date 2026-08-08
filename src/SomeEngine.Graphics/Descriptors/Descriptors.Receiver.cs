using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<ResourceBindingType> slotTypes,
        string? label = null);

    uint GetDescriptorIndex(DescriptorTable table, uint slot);

    void WriteDescriptor(
        DescriptorTable table,
        uint slot,
        in ResourceBinding value);

    PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        in ParameterBlockBindings bindings,
        string? label = null);

    void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings);

    void PublishDescriptors(Device device);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<ResourceBindingType> slotTypes,
        string? label = null) =>
        Receiver.CreateDescriptorTable(device, slotTypes, label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetDescriptorIndex(DescriptorTable table, uint slot) =>
        Receiver.GetDescriptorIndex(table, slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDescriptor(DescriptorTable table, uint slot, in ResourceBinding value) =>
        Receiver.WriteDescriptor(table, slot, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        in ParameterBlockBindings bindings,
        string? label = null) =>
        Receiver.CreatePersistentParameterBindings(device, bindings, label);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings) =>
        Receiver.UpdatePersistentParameterBindings(destination, bindings);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PublishDescriptors(Device device) => Receiver.PublishDescriptors(device);
}
