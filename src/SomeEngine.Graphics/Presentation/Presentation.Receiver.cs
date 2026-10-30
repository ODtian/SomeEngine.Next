namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    /// <summary>Queries the formats, color spaces and presentation modes before creating a swapchain.</summary>
    ReadOnlyMemory<SwapchainSupport> GetSwapchainSupport(Device device, Surface surface);

    /// <summary>Creates a swapchain owned by the Device's Graphics Queue at index zero.</summary>
    Swapchain CreateSwapchain(Device device, in SwapchainDesc desc);

    SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image);

    PresentStatus Present(Queue queue, in SwapchainImage image);

    ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config);
}
