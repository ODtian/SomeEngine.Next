namespace SomeEngine.Graphics.Vulkan;

internal interface IVulkanRetained
{
    void RetainNative();
    void ReleaseNative();
}

internal sealed class VulkanLifetime(Action destroy)
{
    private Action? _destroy = destroy ?? throw new ArgumentNullException(nameof(destroy));
    private int _references = 1;

    internal void Retain()
    {
        int current = Volatile.Read(ref _references);
        while (current > 0)
        {
            int exchanged = Interlocked.CompareExchange(
                ref _references,
                checked(current + 1),
                current);
            if (exchanged == current)
                return;
            current = exchanged;
        }
        throw new ObjectDisposedException(nameof(IVulkanRetained));
    }

    internal void Release()
    {
        int remaining = Interlocked.Decrement(ref _references);
        if (remaining > 0)
            return;
        if (remaining < 0)
            throw new InvalidOperationException("A Vulkan native lifetime was released more than once.");
        Interlocked.Exchange(ref _destroy, null)?.Invoke();
    }
}
