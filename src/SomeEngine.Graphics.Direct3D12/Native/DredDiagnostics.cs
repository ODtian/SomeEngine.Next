using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    internal GraphicsDiagnostic[] CaptureDredDiagnostics()
    {
        List<GraphicsDiagnostic> diagnostics = [];
        CaptureDredDeviceState(diagnostics);
        CaptureDredBreadcrumbs(diagnostics);
        return diagnostics.ToArray();
    }

    private void CaptureDredDeviceState(List<GraphicsDiagnostic> diagnostics)
    {
        try
        {
            using ID3D12DeviceRemovedExtendedData2 dred2 =
                _native.Device.QueryInterface<ID3D12DeviceRemovedExtendedData2>();
            DredPageFaultOutput2 pageFault = dred2.PageFaultAllocationOutput2;
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Information,
                "D3D12 DRED DeviceState",
                $"state={dred2.DeviceState}"));
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Information,
                "D3D12 DRED PageFault",
                $"virtualAddress=0x{pageFault.PageFaultVA:X16}; flags={pageFault.PageFaultFlags}; " +
                $"existingAllocations={(pageFault.HeadExistingAllocationNode == 0 ? "none" : "available")}; " +
                $"recentlyFreedAllocations={(pageFault.HeadRecentFreedAllocationNode == 0 ? "none" : "available")}"));
        }
        catch (Exception exception)
        {
            string message =
                $"DRED2 unavailable; nativeCode=0x{unchecked((uint)exception.HResult):X8}; message={exception.Message}";
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Warning,
                "D3D12 DRED DeviceState",
                message,
                exception.HResult));
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Warning,
                "D3D12 DRED PageFault",
                message,
                exception.HResult));
        }
    }

    private void CaptureDredBreadcrumbs(List<GraphicsDiagnostic> diagnostics)
    {
        try
        {
            using ID3D12DeviceRemovedExtendedData1 dred1 =
                _native.Device.QueryInterface<ID3D12DeviceRemovedExtendedData1>();
            DredAutoBreadcrumbsOutput1 breadcrumbs = new();
            dred1.GetAutoBreadcrumbsOutput1(out breadcrumbs).CheckError();
            List<string> nodes = DescribeBreadcrumbs(breadcrumbs);
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Information,
                "D3D12 DRED Breadcrumbs",
                nodes.Count == 0 ? "nodeCount=0" : $"nodeCount={nodes.Count}; {string.Join(" | ", nodes)}"));

            DredPageFaultOutput1 pageFault = new();
            dred1.GetPageFaultAllocationOutput1(out pageFault).CheckError();
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Information,
                "D3D12 DRED AllocationContext",
                $"virtualAddress=0x{pageFault.PageFaultVA:X16}; " +
                $"existing={DescribeAllocationChain(pageFault.HeadExistingAllocationNode)}; " +
                $"recentlyFreed={DescribeAllocationChain(pageFault.HeadRecentFreedAllocationNode)}"));
        }
        catch (Exception exception)
        {
            string message =
                $"DRED1 unavailable; nativeCode=0x{unchecked((uint)exception.HResult):X8}; message={exception.Message}";
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Warning,
                "D3D12 DRED Breadcrumbs",
                message,
                exception.HResult));
            diagnostics.Add(new GraphicsDiagnostic(
                GraphicsDiagnosticSeverity.Warning,
                "D3D12 DRED AllocationContext",
                message,
                exception.HResult));
        }

    }

    private static List<string> DescribeBreadcrumbs(DredAutoBreadcrumbsOutput1 breadcrumbs)
    {
        List<string> nodes = [];
        HashSet<AutoBreadcrumbNode1> visited = new(ReferenceEqualityComparer.Instance);
        for (AutoBreadcrumbNode1? node = breadcrumbs.HeadAutoBreadcrumbNode;
             node is not null && visited.Add(node);
             node = node.Next)
        {
            string contexts = node.BreadcrumbContexts is { Length: > 0 }
                ? string.Join(",", node.BreadcrumbContexts.Select(static context =>
                    $"{context.BreadcrumbIndex}:{context.ContextString}"))
                : "none";
            nodes.Add(
                $"queue={node.CommandQueueDebugName ?? "<unnamed>"}; " +
                $"list={node.CommandListDebugName ?? "<unnamed>"}; " +
                $"completed={node.LastBreadcrumbValue?.ToString() ?? "unknown"}/{node.BreadcrumbCount}; " +
                $"contexts={contexts}");
        }
        return nodes;
    }

    private static string DescribeAllocationChain(DredAllocationNode1? head)
    {
        if (head is null) return "none";
        List<string> values = [];
        HashSet<DredAllocationNode1> visited = new(ReferenceEqualityComparer.Instance);
        for (DredAllocationNode1? node = head; node is not null && visited.Add(node); node = node.Next)
            values.Add($"{node.AllocationType}:{node.ObjectName ?? "<unnamed>"}");
        return string.Join(",", values);
    }
}
