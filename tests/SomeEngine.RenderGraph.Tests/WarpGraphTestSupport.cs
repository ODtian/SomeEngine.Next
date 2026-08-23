using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit.Sdk;

namespace SomeEngine.RenderGraph.Tests;

internal sealed class WarpGraphTestSupport : IDisposable
{
    private WarpGraphTestSupport(
        IGraphicsBackend backend,
        Device device,
        Queue graphicsQueue,
        Queue computeQueue,
        Queue copyQueue)
    {
        Backend = backend;
        Device = device;
        GraphicsQueue = graphicsQueue;
        ComputeQueue = computeQueue;
        CopyQueue = copyQueue;
    }

    internal IGraphicsBackend Backend { get; }
    internal Device Device { get; }
    internal Queue GraphicsQueue { get; }
    internal Queue ComputeQueue { get; }
    internal Queue CopyQueue { get; }

    internal static WarpGraphTestSupport Create()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("D3D12 WARP requires Windows.");
        IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        try
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
            AdapterInfo warp = adapters.FirstOrDefault(static value => !value.HardwareAccelerated);
            if (warp.Name.Length == 0)
                throw SkipException.ForSkip("The D3D12 WARP adapter is unavailable.");

            DeviceQueueDesc[] queues =
            [
                new(QueueType.Graphics),
                new(QueueType.Compute),
                new(QueueType.Copy),
            ];
            Device device = backend.CreateDevice(new DeviceDesc(
                warp.Id,
                queues,
                label: "RenderGraph WARP test"));
            return new WarpGraphTestSupport(
                backend,
                device,
                backend.GetQueue(device, QueueType.Graphics),
                backend.GetQueue(device, QueueType.Compute),
                backend.GetQueue(device, QueueType.Copy));
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    internal void Wait(ReadOnlySpan<QueueCompletion> completions)
    {
        foreach (ref readonly QueueCompletion completion in completions)
        {
            try
            {
                if (Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                    throw new TimeoutException("A WARP Queue did not complete within ten seconds.");
            }
            catch (NullReferenceException)
            {
                // default QueueCompletion marks an unused Queue slot.
            }
        }
    }

    public void Dispose()
    {
        Backend.CollectCompleted(Device);
        Device.Dispose();
        Backend.Dispose();
    }
}
