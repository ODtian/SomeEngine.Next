using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.RenderGraph.Tests;

internal static class WarpGraphTestSupport
{
    internal static Device CreateDevice(IGraphicsBackend backend)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }

        AdapterInfo warp = adapters.FirstOrDefault(static adapter => !adapter.HardwareAccelerated);
        if (warp.Name.Length == 0)
            throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        return backend.CreateDevice(new DeviceDesc(
            warp.Id,
            queues,
            label: "Render Graph WARP device"));
    }

    internal static void WaitAll(
        IGraphicsBackend backend,
        ReadOnlySpan<QueueCompletion> completions)
    {
        foreach (ref readonly QueueCompletion completion in completions)
        {
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
    }
}
