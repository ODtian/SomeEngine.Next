namespace SomeEngine.Graphics.Vulkan;
internal interface IVulkanRetained
{
    void RetainNative();
    void ReleaseNative();
}

internal sealed class VulkanLifetime(Action destroy)
{
    private Action? _destroy = destroy!;
    private int _references = 1;
    internal void Retain()
    {
        Interlocked.Increment(ref _references);
    }

    internal void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining > 0)
            return;
        Interlocked.Exchange(ref _destroy, null)?.Invoke();
    }
}
