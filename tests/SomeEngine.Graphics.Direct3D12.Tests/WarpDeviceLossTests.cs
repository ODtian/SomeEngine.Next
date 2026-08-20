using Silk.NET.Direct3D12;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Trait("Isolation", "DeviceLossProcess")]
public sealed unsafe class WarpDeviceLossTests
{
    [Theory]
    [InlineData(unchecked((int)0x8007000E))]
    [InlineData(unchecked((int)0x80070008))]
    [InlineData(unchecked((int)0x887A000E))]
    public void Every_documented_native_memory_failure_has_out_of_memory_priority(int nativeCode)
    {
        GraphicsException ordinary = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(null, nativeCode, pipelineCreation: false));
        GraphicsException pipeline = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(null, nativeCode, pipelineCreation: true));

        Assert.Equal(GraphicsError.OutOfMemory, ordinary.Error);
        Assert.Equal(nativeCode, ordinary.NativeCode);
        Assert.Equal(GraphicsError.OutOfMemory, pipeline.Error);
        Assert.Equal(nativeCode, pipeline.NativeCode);
    }

    [Fact]
    public void Invalid_argument_is_classified_by_operation_type()
    {
        const int eInvalidArg = unchecked((int)0x80070057);
        GraphicsException ordinary = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(null, eInvalidArg, pipelineCreation: false));
        GraphicsException pipeline = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(null, eInvalidArg, pipelineCreation: true));

        Assert.Equal(GraphicsError.NativeFailure, ordinary.Error);
        Assert.Equal(eInvalidArg, ordinary.NativeCode);
        Assert.Equal(GraphicsError.PipelineCreation, pipeline.Error);
        Assert.Equal(eInvalidArg, pipeline.NativeCode);
    }

    [Fact]
    public void Queried_invalid_call_is_a_removal_reason_but_direct_invalid_call_is_not()
    {
        const int dxgiErrorInvalidCall = unchecked((int)0x887A0001);
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.True((bool)D3D12PrivateState.InvokeStatic(
            "IsDeviceRemovedReason",
            (long)dxgiErrorInvalidCall));
        Assert.False((bool)D3D12PrivateState.InvokeStatic(
            "IsDirectDeviceRemovalCode",
            (long)dxgiErrorInvalidCall));
        Assert.Contains(
            "DXGI_ERROR_INVALID_CALL",
            (string)D3D12PrivateState.InvokeStatic(
                "FormatDeviceRemovalDiagnostic",
                (long)dxgiErrorInvalidCall),
            StringComparison.Ordinal);

        GraphicsException direct = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(
                device,
                dxgiErrorInvalidCall,
                pipelineCreation: false));
        Assert.Equal(GraphicsError.NativeFailure, direct.Error);
        Assert.Equal(dxgiErrorInvalidCall, direct.NativeCode);
        Assert.False(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Active, device.Status);

        GraphicsException pipeline = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(
                device,
                dxgiErrorInvalidCall,
                pipelineCreation: true));
        Assert.Equal(GraphicsError.PipelineCreation, pipeline.Error);
        Assert.Equal(dxgiErrorInvalidCall, pipeline.NativeCode);
        Assert.False(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Active, device.Status);

        device.Dispose();
        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Public_wait_native_failure_remains_ordinary_when_removal_query_does_not_confirm_loss()
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);

        using (D3D12PrivateState.ReplaceFenceWithSetEventFailure(queue))
        {
            GraphicsException failure = Assert.Throws<GraphicsException>(() =>
                backend.WaitCpu(
                    new QueueCompletion(queue, 1),
                    TimeSpan.FromMilliseconds(10)));
            Assert.Equal(GraphicsError.NativeFailure, failure.Error);
            Assert.Equal(unchecked((int)0x80004005), failure.NativeCode);
        }
        Assert.False(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Active, device.Status);

        device.Dispose();
        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Theory]
    [InlineData(unchecked((int)0x887A0005))]
    [InlineData(unchecked((int)0x887A0006))]
    [InlineData(unchecked((int)0x887A0007))]
    [InlineData(unchecked((int)0x887A0020))]
    public void Removal_shaped_trigger_remains_ordinary_when_the_queried_reason_is_success(
        int triggeringCode)
    {
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeQueriedResultAuthority(
                device,
                triggeringCode));

        Assert.Equal(GraphicsError.NativeFailure, failure.Error);
        Assert.Equal(triggeringCode, failure.NativeCode);
        Assert.False(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Active, device.Status);

        device.Dispose();
        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Direct_removal_failure_publishes_loss_and_preserves_the_operation_code()
    {
        const int dxgiErrorDeviceHung = unchecked((int)0x887A0006);
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);

        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeResultAuthority(
                device,
                dxgiErrorDeviceHung,
                pipelineCreation: false));

        Assert.Equal(GraphicsError.DeviceLost, failure.Error);
        Assert.Equal(dxgiErrorDeviceHung, failure.NativeCode);
        Assert.True(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Lost, device.Status);

        device.Dispose();
        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    [Fact]
    public void Dred_output_is_copied_into_a_structured_managed_report()
    {
        string queueName = "test queue";
        string listName = "test command list";
        string contextText = "render pass: lighting";
        string existingName = "live render target";
        string freedName = "retired upload page";
        fixed (char* queue = queueName)
        fixed (char* list = listName)
        fixed (char* context = contextText)
        fixed (char* existing = existingName)
        fixed (char* freed = freedName)
        {
            uint completed = 2;
            AutoBreadcrumbOp* history = stackalloc AutoBreadcrumbOp[3]
            {
                AutoBreadcrumbOp.Drawinstanced,
                AutoBreadcrumbOp.Dispatch,
                AutoBreadcrumbOp.Copyresource,
            };
            DredBreadcrumbContext* contexts = stackalloc DredBreadcrumbContext[1];
            contexts[0] = new DredBreadcrumbContext(1, context);
            AutoBreadcrumbNode1 breadcrumb = new()
            {
                PCommandQueueDebugNameW = queue,
                PCommandListDebugNameW = list,
                BreadcrumbCount = 3,
                PLastBreadcrumbValue = &completed,
                PCommandHistory = history,
                BreadcrumbContextsCount = 1,
                PBreadcrumbContexts = contexts,
            };
            DredAllocationNode1 existingAllocation = new()
            {
                ObjectNameW = existing,
                AllocationType = (DredAllocationType)1,
            };
            DredAllocationNode1 freedAllocation = new()
            {
                ObjectNameW = freed,
                AllocationType = (DredAllocationType)2,
            };
            DredAutoBreadcrumbsOutput1 breadcrumbs = new()
            {
                PHeadAutoBreadcrumbNode = &breadcrumb,
            };
            DredPageFaultOutput1 pageFault = new()
            {
                PageFaultVA = 0x1234_5678_9ABC_DEF0,
                PHeadExistingAllocationNode = &existingAllocation,
                PHeadRecentFreedAllocationNode = &freedAllocation,
            };

            D3D12DeviceLossReport report = Assert.IsType<D3D12DeviceLossReport>(
                D3D12PrivateState.InvokeStatic("BuildDredReport", breadcrumbs, pageFault));

            Assert.Equal(0, report.BreadcrumbQueryResult);
            Assert.Equal(0, report.PageFaultQueryResult);
            Assert.Equal(pageFault.PageFaultVA, report.PageFaultAddress);
            Assert.False(report.BreadcrumbsTruncated);
            Assert.False(report.BreadcrumbContextsTruncated);
            Assert.False(report.ExistingAllocationsTruncated);
            Assert.False(report.RecentlyFreedAllocationsTruncated);
            D3D12BreadcrumbReport copied = Assert.Single(report.Breadcrumbs);
            Assert.Equal(queueName, copied.CommandQueue);
            Assert.Equal(listName, copied.CommandList);
            Assert.Equal(2u, copied.CompletedBreadcrumbCount);
            Assert.Equal(3u, copied.TotalBreadcrumbCount);
            Assert.Equal(AutoBreadcrumbOp.Dispatch.ToString(), copied.LastOperation);
            Assert.Equal(contextText, Assert.Single(copied.Contexts));
            Assert.Equal(existingName, Assert.Single(report.ExistingAllocations).Name);
            Assert.Equal(freedName, Assert.Single(report.RecentlyFreedAllocations).Name);
            Assert.Contains("0x123456789ABCDEF0", report.Text, StringComparison.Ordinal);
            Assert.Contains(listName, report.Text, StringComparison.Ordinal);
            Assert.Contains(contextText, report.Text, StringComparison.Ordinal);
            Assert.Contains(existingName, report.Text, StringComparison.Ordinal);
            Assert.Contains(freedName, report.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Dred_report_preserves_partial_query_results_and_marks_truncation()
    {
        const int eFail = unchecked((int)0x80004005);
        const int nodeCount = 257;
        AutoBreadcrumbNode1* nodes = stackalloc AutoBreadcrumbNode1[nodeCount];
        for (int index = 0; index < nodeCount - 1; index++)
            nodes[index].PNext = &nodes[index + 1];
        DredAutoBreadcrumbsOutput1 breadcrumbs = new()
        {
            PHeadAutoBreadcrumbNode = nodes,
        };

        D3D12DeviceLossReport report = Assert.IsType<D3D12DeviceLossReport>(
            D3D12PrivateState.InvokeStatic(
                "BuildDredReport",
                breadcrumbs,
                0,
                default(DredPageFaultOutput1),
                eFail));

        Assert.Equal(0, report.BreadcrumbQueryResult);
        Assert.Equal(eFail, report.PageFaultQueryResult);
        Assert.Equal(nodeCount - 1, report.Breadcrumbs.Length);
        Assert.True(report.BreadcrumbsTruncated);
        Assert.Empty(report.ExistingAllocations);
        Assert.Empty(report.RecentlyFreedAllocations);
        Assert.Contains("0x80004005", report.Text, StringComparison.Ordinal);
        Assert.Contains("truncated", report.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_native_removal_publishes_once_and_abandons_all_retirement_families()
    {
        using D3D12TestWindow window = new();
        var backend = new D3D12Backend();
        Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Queue queue = backend.GetQueue(device, QueueType.Graphics, 0);

        _ = backend.Submit(queue, new QueueSubmitDesc([], [], [], [], []));
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                64 * 1024,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = backend.EnqueueMakeResident(
            queue,
            [backend.GetResidencyResource(buffer)]);

        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle));
        SwapchainConfig config = new(
            32,
            32,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            false,
            2);
        Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.ColorAttachment, config));
        SubmitAndPresent(backend, device, swapchain, queue);
        swapchain.Dispose();

        QueueRetirementSnapshot pending = D3D12PrivateState.QueueRetirements(queue);
        Assert.True(pending.PendingSubmissionCount > 0);
        Assert.Equal(1, pending.PendingPresentationCount);
        Assert.Equal(1, pending.PendingCapabilityCount);
        Assert.True(pending.PresentationNativeReferenceCount > 0);
        Assert.True(pending.CapabilityNativeReferenceCount > 0);

        backend.GetNativeDevice(device)->RemoveDevice();
        int removalReason = backend.GetNativeDevice(device)->GetDeviceRemovedReason();
        Assert.True((bool)D3D12PrivateState.InvokeStatic(
            "IsDeviceRemovedReason",
            (long)removalReason));
        const int eFail = unchecked((int)0x80004005);
        GraphicsException loss = Assert.Throws<GraphicsException>(() =>
            D3D12PrivateState.InvokeQueriedResultAuthority(
                device,
                eFail));
        Assert.Equal(GraphicsError.DeviceLost, loss.Error);
        Assert.Equal(eFail, loss.NativeCode);
        Assert.Contains("GetDeviceRemovedReason", loss.Diagnostic, StringComparison.Ordinal);
        Assert.Contains($"0x{unchecked((uint)removalReason):X8}", loss.Diagnostic, StringComparison.Ordinal);
        Assert.True(D3D12PrivateState.NativeDeviceLossConfirmed(device));
        Assert.Equal(DeviceStatus.Lost, device.Status);
        Assert.Same(loss, diagnostics.DeviceLoss);
        if (diagnostics.DeviceLossReport is D3D12DeviceLossReport report)
        {
            Assert.Same(report, diagnostics.DeviceLossReport);
            Assert.Contains("DRED page-fault VA", report.Text, StringComparison.Ordinal);
            Assert.Contains("DRED page-fault VA", loss.Diagnostic, StringComparison.Ordinal);
        }

        device.Dispose();
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Null(diagnostics.TeardownFailure);
        QueueRetirementSnapshot abandoned = D3D12PrivateState.QueueRetirements(queue);
        Assert.Equal(0, abandoned.PendingSubmissionCount);
        Assert.Equal(0, abandoned.UntrustedSubmissionCount);
        Assert.Equal(0, abandoned.PendingPresentationCount);
        Assert.Equal(0, abandoned.UntrustedPresentationCount);
        Assert.Equal(0, abandoned.PendingCapabilityCount);
        Assert.Equal(0, abandoned.UntrustedCapabilityCount);
        Assert.False(abandoned.HasNativeQueue);
        Assert.False(abandoned.HasFence);

        backend.Dispose();
        Assert.False(D3D12PrivateState.IsRuntimeQuarantined(backend));
    }

    private static void SubmitAndPresent(
        D3D12Backend backend,
        Device device,
        Swapchain swapchain,
        Queue queue)
    {
        Assert.Equal(
            SwapchainAcquireStatus.Success,
            backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2)),
                out SwapchainImage image));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context, default);
        backend.Barrier(context, new TextureBarrier(
            image.Texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            image.InitialSync,
            PipelineSync.None,
            image.InitialAccess,
            ResourceAccess.NoAccess,
            image.InitialLayout,
            TextureLayout.Present));
        using RecordedCommands commands = backend.End(context);
        _ = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [image], []));
        _ = backend.Present(queue, image);
    }
}
