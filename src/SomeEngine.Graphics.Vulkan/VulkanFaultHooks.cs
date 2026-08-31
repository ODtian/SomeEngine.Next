#if SOMEENGINE_TESTING
namespace SomeEngine.Graphics.Vulkan;

internal enum VulkanCallPoint
{
    QueueSubmit,
    QueuePresent,
    QueueBindSparse,
    WaitSemaphores,
    GetSemaphoreCounter,
    CreatePipeline,
    CreateSwapchain,
    AllocateMemory,
}

internal sealed class VulkanFaultHooks
{
    internal Func<VulkanCallPoint, Result, Result>? OverrideResult;
    internal Action<VulkanCallPoint>? BeforeCall;
    internal Action<VulkanCallPoint>? AfterCall;

    internal void Before(VulkanCallPoint point) =>
        Volatile.Read(ref BeforeCall)?.Invoke(point);

    internal bool TryOverride(VulkanCallPoint point, out Result result)
    {
        Func<VulkanCallPoint, Result, Result>? overrideResult =
            Volatile.Read(ref OverrideResult);
        result = overrideResult?.Invoke(point, Result.Success) ?? Result.Success;
        return result != Result.Success;
    }

    internal void After(VulkanCallPoint point) =>
        Volatile.Read(ref AfterCall)?.Invoke(point);

    internal void Reset()
    {
        Volatile.Write(ref OverrideResult, null);
        Volatile.Write(ref BeforeCall, null);
        Volatile.Write(ref AfterCall, null);
    }
}
#endif
