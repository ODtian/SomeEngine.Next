using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
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

    internal static WarpGraphTestSupport Create(bool enableValidation = false,
        DeviceFeatures requiredFeatures = DeviceFeatures.None)
    {
        IGraphicsBackend native = OperatingSystem.IsWindows()
            ? D3D12GraphicsBackend.Create() : VulkanGraphicsBackend.Create();
        IGraphicsBackend backend = enableValidation
            ? new ValidationLayer(native, new ValidationOptions(NativeValidationDisabled: !OperatingSystem.IsWindows()))
            : native;
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
            DeviceQueueDesc[] queues =
            [
                new(QueueType.Graphics),
                new(QueueType.Compute),
                new(QueueType.Copy),
            ];
            Device? device = null;
            foreach (AdapterInfo adapter in adapters.OrderBy(static value => value.HardwareAccelerated))
            {
                if (OperatingSystem.IsWindows() && adapter.HardwareAccelerated) continue;
                try
                {
                    device = backend.CreateDevice(new DeviceDesc(adapter.Id, queues,
                        requiredFeatures: requiredFeatures, label: "RenderGraph behavior test"));
                    break;
                }
                catch (NotSupportedException) when (!OperatingSystem.IsWindows() && !adapter.HardwareAccelerated)
                {
                }
            }
            if (device is null)
                throw SkipException.ForSkip("An adapter with the required graph queues is unavailable.");
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
            if (completion == default) continue;
            if (Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed)
                throw new TimeoutException("A graph Queue did not complete within ten seconds.");
        }
    }

    public void Dispose()
    {
        Backend.CollectCompleted(Device);
        Device.Dispose();
        Backend.Dispose();
    }
}
