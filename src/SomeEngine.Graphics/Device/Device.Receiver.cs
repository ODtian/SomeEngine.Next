using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Queue GetQueue(Device device, QueueType type, uint index = 0);

    bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability;

    void CollectCompleted(Device device);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Queue GetQueue(Device device, QueueType type, uint index = 0) =>
        Receiver.GetQueue(device, type, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability =>
        Receiver.TryGetCapability(device, out capability);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CollectCompleted(Device device) => Receiver.CollectCompleted(device);
}
