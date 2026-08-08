using System.Runtime.CompilerServices;

namespace SomeEngine.Graphics;

public partial interface IGraphicsBackend
{
    Swapchain CreateSwapchain(Device device, in SwapchainDesc desc);

    SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image);

    PresentStatus Present(Queue queue, in SwapchainImage image);

    ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config);
}

public sealed partial class Graphics<TBackend>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Swapchain CreateSwapchain(Device device, in SwapchainDesc desc) =>
        Receiver.CreateSwapchain(device, desc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image) =>
        Receiver.Acquire(swapchain, options, out image);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PresentStatus Present(Queue queue, in SwapchainImage image) =>
        Receiver.Present(queue, image);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config) =>
        Receiver.Reconfigure(swapchain, config);
}
