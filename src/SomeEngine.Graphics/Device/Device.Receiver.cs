namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Queue GetQueue(Device device, QueueType type, uint index = 0);

    /// <summary>
    /// Returns the authoritative post-creation capability object for <paramref name="device"/>.
    /// Device creation feature flags are requests only and must not be used as runtime authority.
    /// </summary>
    bool TryGetCapability<TCapability>(
        Device device,
        out TCapability? capability)
        where TCapability : DeviceCapability;

    void CollectCompleted(Device device);
}
