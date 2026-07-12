using System.ComponentModel;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using Vortice.Direct3D12;
using Xunit;
using RenderGraphRuntime = SomeEngine.RenderGraph.RenderGraph;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class SwapchainTests
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint DesktopSwitchDesktop = 0x0100;

    [Fact]
    public void Warp_render_graph_clears_acquired_backbuffer_and_returns_it_to_present()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP render-graph presentation lane must run; it may not silently skip.");

        using HiddenWindow window = new(320, 200);
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            320,
            200,
            Format.R8G8B8A8UNorm,
            BufferCount: 2,
            PresentMode: SwapchainPresentMode.Fifo,
            Name: "warp.render-graph-swapchain"));

        SwapchainImage image = device.AcquireNextImage(swapchain);
        using (RenderGraphRuntime graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        }))
        {
            GraphBuilder builder = graph.Begin();
            TextureId backbuffer = builder.ImportTexture(
                image.Texture,
                ResourceState.Present,
                ResourceState.Present,
                contentsAvailable: false);
            TextureViewId backbufferView = builder.CreateTextureView(
                backbuffer,
                TextureSubresourceRange.WholeColor,
                TextureViewUsage.ColorAttachment,
                name: "warp.render-graph-backbuffer-rtv");
            PassBuilder clear = builder.AddPass("clear-acquired-backbuffer", QueueSelection.Graphics);
            _ = clear.ColorAttachment(
                0,
                backbufferView,
                LoadAction.Clear,
                new Vector4(0.125f, 0.5f, 0.875f, 1.0f));
            clear.Execute(static (ICommandContext _, in PassResources _) => { });

            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotEmpty(execution.Completions);
        }

        PresentResult present = device.Present(swapchain, image.ImageIndex);
        Assert.Contains(present.Status, new[] { PresentStatus.Success, PresentStatus.Occluded });
        device.DestroySwapchain(swapchain);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    [Fact]
    public void Warp_smoke_exercises_native_backbuffer_state_and_resize()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP swapchain lane must run; it may not silently skip.");

        using HiddenWindow window = new(320, 200);
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });

        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            320,
            200,
            Format.R8G8B8A8UNorm,
            BufferCount: 2,
            PresentMode: SwapchainPresentMode.Fifo,
            Name: "warp.native-swapchain"));
        device.Resize(swapchain, 321, 201);
        device.Resize(swapchain, 320, 200);

        SwapchainImage first = device.AcquireNextImage(swapchain);
        Assert.Throws<InvalidOperationException>(() => device.AcquireNextImage(swapchain));
        TextureMetadata firstMetadata = device.GetTextureMetadata(first.Texture);
        Assert.Equal(320, firstMetadata.Description.Width);
        Assert.Equal(200, firstMetadata.Description.Height);
        Assert.Equal(Format.R8G8B8A8UNorm, firstMetadata.Description.Format);
        Assert.True((firstMetadata.Description.Usage & TextureUsage.ColorAttachment) != 0);
        Assert.Throws<InvalidOperationException>(() => device.DestroyTexture(first.Texture));
        Assert.Throws<InvalidOperationException>(() => device.Resize(swapchain, 640, 360));

        PresentResult firstPresent = device.Present(swapchain, first.ImageIndex);
        Assert.Contains(firstPresent.Status, new[] { PresentStatus.Success, PresentStatus.Occluded });
        Assert.Equal(DeviceErrorKind.None, device.LastError.Kind);

        SwapchainImage beforeResize = device.AcquireNextImage(swapchain);
        PresentResult secondPresent = device.Present(swapchain, beforeResize.ImageIndex);
        Assert.Contains(secondPresent.Status, new[] { PresentStatus.Success, PresentStatus.Occluded });

        device.Resize(swapchain, 640, 360);
        Assert.Throws<ArgumentException>(() => device.GetTextureMetadata(beforeResize.Texture));
        SwapchainImage resized = device.AcquireNextImage(swapchain);
        TextureMetadata resizedMetadata = device.GetTextureMetadata(resized.Texture);
        Assert.Equal(640, resizedMetadata.Description.Width);
        Assert.Equal(360, resizedMetadata.Description.Height);
        Assert.Equal(Format.R8G8B8A8UNorm, resizedMetadata.Description.Format);
        PresentResult resizedPresent = device.Present(swapchain, resized.ImageIndex);
        Assert.Contains(resizedPresent.Status, new[] { PresentStatus.Success, PresentStatus.Occluded });

        device.DestroySwapchain(swapchain);
        Assert.Throws<ArgumentException>(() => device.AcquireNextImage(swapchain));
        Assert.Throws<ArgumentException>(() => device.GetTextureMetadata(resized.Texture));
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    [Fact]
    public void Display_hwnd_exercises_vsync_tearing_occlusion_hdr_and_device_removed_status()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required hardware/display swapchain lane must run on Windows; it may not silently skip.");

        nint inputDesktop = OpenInputDesktop(
            0,
            false,
            DesktopReadObjects | DesktopCreateWindow | DesktopWriteObjects | DesktopSwitchDesktop);
        if (inputDesktop == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenInputDesktop failed for the hardware display lane.");

        ExceptionDispatchInfo? failure = null;
        Thread worker = new(() =>
        {
            try
            {
                if (!SetThreadDesktop(inputDesktop))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadDesktop(input) failed for the hardware display lane.");
                ExecuteHardwareDisplayScenario();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SomeEngine hardware display lane",
        };

        try
        {
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(60)))
                throw new TimeoutException("The hardware display lane did not finish within 60 seconds.");
            failure?.Throw();
        }
        finally
        {
            if (!CloseDesktop(inputDesktop))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CloseDesktop failed after the hardware display lane.");
        }
    }

    private static void ExecuteHardwareDisplayScenario()
    {
        using (Device device = CreateHardwareDevice())
        {
            Assert.True(device.Info.HardwareAccelerated, "The display lane must not fall back to WARP.");
            ExerciseFifoPresentation(device);
            ExerciseTearingPresentation(device);
            ExerciseOccludedPresentation(device);
            Assert.DoesNotContain(
                device.DrainDiagnostics(),
                static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        }

        using (Device hdrDevice = CreateHardwareDevice())
            ExerciseHdrPresentation(hdrDevice);

        using (Device removedDevice = CreateHardwareDevice())
            ExerciseDeviceRemovalPresentation(removedDevice);
    }

    private static void ExerciseFifoPresentation(Device device)
    {
        using VisibleWindow window = CreateVisibleWindow();
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            400,
            240,
            Format.R8G8B8A8UNorm,
            PresentMode: SwapchainPresentMode.Fifo,
            Name: "display.vsync"));
        PresentResult result = PresentUntilVisible(
            device,
            swapchain,
            new PresentOptions(VSync: true, AllowTearing: false),
            window);
        Assert.True(result.Status == PresentStatus.Success, $"FIFO presentation remained {result.Status}. {window.DescribeVisibility()}");
        device.DestroySwapchain(swapchain);
    }

    private static void ExerciseTearingPresentation(Device device)
    {
        using VisibleWindow window = CreateVisibleWindow();
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            400,
            240,
            Format.R8G8B8A8UNorm,
            PresentMode: SwapchainPresentMode.Immediate,
            AllowTearing: true,
            Name: "display.tearing"));
        PresentResult result = PresentUntilVisible(
            device,
            swapchain,
            new PresentOptions(VSync: false, AllowTearing: true),
            window);
        Assert.True(result.Status == PresentStatus.Success, $"Tearing presentation remained {result.Status}. {window.DescribeVisibility()}");
        device.DestroySwapchain(swapchain);
    }

    private static void ExerciseOccludedPresentation(Device device)
    {
        using VisibleWindow window = CreateVisibleWindow();
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            400,
            240,
            Format.R8G8B8A8UNorm,
            PresentMode: SwapchainPresentMode.Immediate,
            Name: "display.occlusion"));
        window.Minimize();
        PresentStatus status = PresentStatus.Success;
        for (int attempt = 0; attempt < 64 && status != PresentStatus.Occluded; attempt++)
        {
            SwapchainImage image = device.AcquireNextImage(swapchain);
            status = device.Present(
                swapchain,
                image.ImageIndex,
                new PresentOptions(VSync: false, AllowTearing: false)).Status;
            window.PumpMessages();
            if (status != PresentStatus.Occluded) Thread.Sleep(16);
        }
        Assert.True(status == PresentStatus.Occluded, $"Minimized presentation remained {status}. {window.DescribeVisibility()}");
        device.DestroySwapchain(swapchain);
    }

    private static void ExerciseHdrPresentation(Device device)
    {
        using VisibleWindow window = CreateVisibleWindow();
        // An SDR desktop may reject HDR10, but the real DXGI query and SetColorSpace1 path must execute.
        try
        {
            SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
                window.Handle,
                400,
                240,
                Format.R16G16B16A16Float,
                PresentMode: SwapchainPresentMode.Fifo,
                ColorSpace: SwapchainColorSpace.Hdr10,
                Name: "display.hdr10"));
            PresentResult result = PresentUntilVisible(device, swapchain, default, window);
            Assert.True(result.Status == PresentStatus.Success, $"HDR presentation remained {result.Status}. {window.DescribeVisibility()}");
            device.DestroySwapchain(swapchain);
        }
        catch (NotSupportedException exception)
        {
            Assert.Contains("Hdr10", exception.Message, StringComparison.Ordinal);
        }
    }

    private static void ExerciseDeviceRemovalPresentation(Device device)
    {
        using VisibleWindow window = CreateVisibleWindow();
        SwapchainHandle swapchain = device.CreateSwapchain(new SwapchainDesc(
            window.Handle,
            400,
            240,
            Format.R8G8B8A8UNorm,
            PresentMode: SwapchainPresentMode.Immediate,
            Name: "display.device-removed"));
        SwapchainImage image = device.AcquireNextImage(swapchain);
        using (ID3D12Device5 removal = device.NativeDevice.QueryInterface<ID3D12Device5>())
            removal.RemoveDevice();
        PresentResult result = device.Present(swapchain, image.ImageIndex);
        Assert.Equal(PresentStatus.DeviceLost, result.Status);
        Assert.Equal(DeviceErrorKind.DeviceLost, result.Error.Kind);
        Assert.Equal(DeviceErrorKind.DeviceLost, device.LastError.Kind);
    }

    private static VisibleWindow CreateVisibleWindow()
    {
        VisibleWindow window = new(400, 240);
        window.Show();
        return window;
    }

    private static PresentResult PresentUntilVisible(
        Device device,
        SwapchainHandle swapchain,
        PresentOptions options,
        VisibleWindow window)
    {
        PresentResult result = new(PresentStatus.Occluded);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            SwapchainImage image = device.AcquireNextImage(swapchain);
            result = device.Present(swapchain, image.ImageIndex, options);
            if (result.Status != PresentStatus.Occluded) return result;
            window.EnsureVisible();
            if (attempt != 15) Thread.Sleep(16);
        }

        return result;
    }

    private static Device CreateHardwareDevice() => new(new Options
    {
        UseWarpAdapter = false,
        EnableDebugLayer = true,
        EnableGpuValidation = false,
    });

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);

    private sealed class HiddenWindow : IDisposable
    {
        private const uint WsOverlappedWindow = 0x00CF0000;

        public HiddenWindow(int width, int height)
        {
            Handle = CreateWindowExW(
                0,
                "STATIC",
                "SomeEngine D3D12 swapchain test",
                WsOverlappedWindow,
                0,
                0,
                width,
                height,
                0,
                0,
                0,
                0);
            if (Handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed for the native swapchain test window.");
        }

        public nint Handle { get; private set; }

        public void Dispose()
        {
            nint handle = Handle;
            Handle = 0;
            if (handle != 0 && !DestroyWindow(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed for the native swapchain test window.");
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
    }

    private sealed class VisibleWindow : IDisposable
    {
        private static readonly nint HwndTopmost = new(-1);
        private static readonly nint HwndNotTopmost = new(-2);
        private const uint WsExTopmost = 0x00000008;
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const uint WsVisible = 0x10000000;
        private const int SwMinimize = 6;
        private const int SwRestore = 9;
        private const uint SwpShowWindow = 0x0040;
        private const uint RdwInvalidate = 0x0001;
        private const uint RdwAllChildren = 0x0080;
        private const uint RdwUpdateNow = 0x0100;
        private const uint DwmwaCloaked = 14;
        private const uint GaRoot = 2;
        private const uint PmRemove = 0x0001;
        private readonly int _width;
        private readonly int _height;

        public VisibleWindow(int width, int height)
        {
            _width = width;
            _height = height;
            Handle = CreateWindowExW(
                WsExTopmost,
                "STATIC",
                "SomeEngine D3D12 hardware display test",
                WsOverlappedWindow | WsVisible,
                100,
                100,
                width,
                height,
                0,
                0,
                0,
                0);
            if (Handle == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed for the display test window.");
        }

        public nint Handle { get; private set; }

        public void Show() => EnsureVisible();

        public void EnsureVisible()
        {
            _ = ShowWindow(Handle, SwRestore);
            if (!SetWindowPos(Handle, HwndNotTopmost, 100, 100, _width, _height, SwpShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos(NOTOPMOST) failed for the display test window.");
            if (!SetWindowPos(Handle, HwndTopmost, 100, 100, _width, _height, SwpShowWindow))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos(TOPMOST) failed for the display test window.");
            _ = BringWindowToTop(Handle);
            _ = SetForegroundWindow(Handle);
            if (!RedrawWindow(Handle, 0, 0, RdwInvalidate | RdwAllChildren | RdwUpdateNow))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RedrawWindow failed for the display test window.");
            if (!UpdateWindow(Handle))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error, "UpdateWindow failed for the display test window.");
            }
            PumpMessages();
            Marshal.ThrowExceptionForHR(DwmFlush());
            PumpMessages();
        }

        public string DescribeVisibility()
        {
            bool visible = IsWindowVisible(Handle);
            bool iconic = IsIconic(Handle);
            bool hasRect = GetWindowRect(Handle, out NativeRect rect);
            NativePoint center = hasRect
                ? new NativePoint(rect.Left + ((rect.Right - rect.Left) / 2), rect.Top + ((rect.Bottom - rect.Top) / 2))
                : default;
            nint pointWindow = hasRect ? WindowFromPoint(center) : 0;
            nint pointRoot = pointWindow == 0 ? 0 : GetAncestor(pointWindow, GaRoot);
            nint foreground = GetForegroundWindow();
            int cloaked = -1;
            int cloakResult = DwmGetWindowAttribute(Handle, DwmwaCloaked, out cloaked, sizeof(int));
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            string rectText = hasRect
                ? $"({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})"
                : $"GetWindowRect failed ({Marshal.GetLastWin32Error()})";
            return $"hwnd=0x{Handle.ToInt64():X}; visible={visible}; iconic={iconic}; rect={rectText}; " +
                $"centerOwner=0x{pointWindow.ToInt64():X}; centerRoot=0x{pointRoot.ToInt64():X}; " +
                $"foreground=0x{foreground.ToInt64():X}; cloaked={cloaked}; cloakHr=0x{unchecked((uint)cloakResult):X8}; " +
                $"session={process.SessionId}; userInteractive={Environment.UserInteractive}.";
        }

        public void Minimize()
        {
            _ = ShowWindow(Handle, SwMinimize);
            PumpMessages();
            Marshal.ThrowExceptionForHR(DwmFlush());
            PumpMessages();
            if (!IsIconic(Handle))
                throw new InvalidOperationException($"The display test window did not enter the minimized state. {DescribeVisibility()}");
            Thread.Sleep(100);
        }

        public void PumpMessages()
        {
            while (PeekMessageW(out NativeMessage message, 0, 0, 0, PmRemove))
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessageW(in message);
            }
        }

        public void Dispose()
        {
            nint handle = Handle;
            Handle = 0;
            if (handle != 0 && !DestroyWindow(handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DestroyWindow failed for the display test window.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativePoint
        {
            public NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }

            public readonly int X;
            public readonly int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeRect
        {
            public readonly int Left;
            public readonly int Top;
            public readonly int Right;
            public readonly int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeMessage
        {
            public readonly nint Window;
            public readonly uint Message;
            public readonly nuint WParam;
            public readonly nint LParam;
            public readonly uint Time;
            public readonly NativePoint Point;
            public readonly uint Private;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BringWindowToTop(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(nint window, nint updateRect, nint updateRegion, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint window, uint flags);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            nint window,
            uint attribute,
            out int value,
            int valueSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(
            out NativeMessage message,
            nint window,
            uint minimum,
            uint maximum,
            uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(in NativeMessage message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessageW(in NativeMessage message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);
    }
}
