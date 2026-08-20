using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>One DRED automatic-breadcrumb command-list report.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable diagnostic values may be shared.</para>
/// <para><b>Ownership:</b> Owns only copied managed strings and arrays.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; copied diagnostic data remains readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class D3D12BreadcrumbReport
{
    internal D3D12BreadcrumbReport(
        string? commandQueue,
        string? commandList,
        uint completedBreadcrumbCount,
        uint totalBreadcrumbCount,
        string? lastOperation,
        ImmutableArray<string> contexts)
    {
        CommandQueue = commandQueue;
        CommandList = commandList;
        CompletedBreadcrumbCount = completedBreadcrumbCount;
        TotalBreadcrumbCount = totalBreadcrumbCount;
        LastOperation = lastOperation;
        Contexts = contexts;
    }

    public string? CommandQueue { get; }
    public string? CommandList { get; }
    public uint CompletedBreadcrumbCount { get; }
    public uint TotalBreadcrumbCount { get; }
    public string? LastOperation { get; }
    public ImmutableArray<string> Contexts { get; }
}

/// <summary>One allocation candidate reported by DRED for a page fault.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable diagnostic values may be shared.</para>
/// <para><b>Ownership:</b> Owns only copied managed strings.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; copied diagnostic data remains readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class D3D12PageFaultAllocationReport
{
    internal D3D12PageFaultAllocationReport(
        string? name,
        string allocationType,
        ulong objectAddress)
    {
        Name = name;
        AllocationType = allocationType;
        ObjectAddress = objectAddress;
    }

    public string? Name { get; }
    public string AllocationType { get; }
    public ulong ObjectAddress { get; }
}

/// <summary>Structured Direct3D 12 device-removal diagnostics captured from DRED.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable diagnostic values may be shared.</para>
/// <para><b>Ownership:</b> Owns copied managed arrays and text; no DRED pointers escape.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; the report remains readable after Device disposal.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public sealed class D3D12DeviceLossReport
{
    internal D3D12DeviceLossReport(
        int breadcrumbQueryResult,
        int pageFaultQueryResult,
        ulong pageFaultAddress,
        ImmutableArray<D3D12BreadcrumbReport> breadcrumbs,
        ImmutableArray<D3D12PageFaultAllocationReport> existingAllocations,
        ImmutableArray<D3D12PageFaultAllocationReport> recentlyFreedAllocations,
        bool breadcrumbsTruncated,
        bool breadcrumbContextsTruncated,
        bool existingAllocationsTruncated,
        bool recentlyFreedAllocationsTruncated,
        string text)
    {
        BreadcrumbQueryResult = breadcrumbQueryResult;
        PageFaultQueryResult = pageFaultQueryResult;
        PageFaultAddress = pageFaultAddress;
        Breadcrumbs = breadcrumbs;
        ExistingAllocations = existingAllocations;
        RecentlyFreedAllocations = recentlyFreedAllocations;
        BreadcrumbsTruncated = breadcrumbsTruncated;
        BreadcrumbContextsTruncated = breadcrumbContextsTruncated;
        ExistingAllocationsTruncated = existingAllocationsTruncated;
        RecentlyFreedAllocationsTruncated = recentlyFreedAllocationsTruncated;
        Text = text;
    }

    public int BreadcrumbQueryResult { get; }
    public int PageFaultQueryResult { get; }
    public ulong PageFaultAddress { get; }
    public ImmutableArray<D3D12BreadcrumbReport> Breadcrumbs { get; }
    public ImmutableArray<D3D12PageFaultAllocationReport> ExistingAllocations { get; }
    public ImmutableArray<D3D12PageFaultAllocationReport> RecentlyFreedAllocations { get; }
    public bool BreadcrumbsTruncated { get; }
    public bool BreadcrumbContextsTruncated { get; }
    public bool ExistingAllocationsTruncated { get; }
    public bool RecentlyFreedAllocationsTruncated { get; }
    public string Text { get; }
}

internal sealed unsafe partial class D3D12Backend
{
    private static void SetNativeName(void* value, string? label)
    {
        if (value is null || string.IsNullOrWhiteSpace(label))
            return;
        fixed (char* name = label)
            _ = ((ID3D12Object*)value)->SetName(name);
    }

    private static D3D12DeviceLossReport? CaptureDredReport(D3D12Device device)
    {
        ID3D12DeviceRemovedExtendedData1* dred = null;
        Guid iid = ID3D12DeviceRemovedExtendedData1.Guid;
        int result = device.Native->QueryInterface(&iid, (void**)&dred);
        if (result < 0 || dred is null)
            return null;
        try
        {
            DredAutoBreadcrumbsOutput1 breadcrumbs = default;
            DredPageFaultOutput1 pageFault = default;
            int breadcrumbResult = dred->GetAutoBreadcrumbsOutput1(&breadcrumbs);
            int pageFaultResult = dred->GetPageFaultAllocationOutput1(&pageFault);
            return BuildDredReport(
                breadcrumbs,
                breadcrumbResult,
                pageFault,
                pageFaultResult);
        }
        finally
        {
            _ = dred->Release();
        }
    }

    private static D3D12DeviceLossReport BuildDredReport(
        DredAutoBreadcrumbsOutput1 breadcrumbs,
        DredPageFaultOutput1 pageFault) =>
        BuildDredReport(breadcrumbs, 0, pageFault, 0);

    private static D3D12DeviceLossReport BuildDredReport(
        DredAutoBreadcrumbsOutput1 breadcrumbs,
        int breadcrumbQueryResult,
        DredPageFaultOutput1 pageFault,
        int pageFaultQueryResult)
    {
        D3D12BreadcrumbReport[] breadcrumbReports = breadcrumbQueryResult >= 0
            ? ReadBreadcrumbs(
                breadcrumbs.PHeadAutoBreadcrumbNode,
                out bool breadcrumbsTruncated,
                out bool contextsTruncated)
            : ReadFailedBreadcrumbs(
                out breadcrumbsTruncated,
                out contextsTruncated);
        D3D12PageFaultAllocationReport[] existing = pageFaultQueryResult >= 0
            ? ReadAllocations(
                pageFault.PHeadExistingAllocationNode,
                out bool existingTruncated)
            : ReadFailedAllocations(out existingTruncated);
        D3D12PageFaultAllocationReport[] freed = pageFaultQueryResult >= 0
            ? ReadAllocations(
                pageFault.PHeadRecentFreedAllocationNode,
                out bool freedTruncated)
            : ReadFailedAllocations(out freedTruncated);
        ulong pageFaultAddress = pageFaultQueryResult >= 0 ? pageFault.PageFaultVA : 0;
        string text = FormatDredReport(
            breadcrumbQueryResult,
            pageFaultQueryResult,
            pageFaultAddress,
            breadcrumbReports,
            existing,
            freed,
            breadcrumbsTruncated,
            contextsTruncated,
            existingTruncated,
            freedTruncated);
        return new D3D12DeviceLossReport(
            breadcrumbQueryResult,
            pageFaultQueryResult,
            pageFaultAddress,
            ImmutableArray.CreateRange(breadcrumbReports),
            ImmutableArray.CreateRange(existing),
            ImmutableArray.CreateRange(freed),
            breadcrumbsTruncated,
            contextsTruncated,
            existingTruncated,
            freedTruncated,
            text);
    }

    private static D3D12BreadcrumbReport[] ReadFailedBreadcrumbs(
        out bool breadcrumbsTruncated,
        out bool contextsTruncated)
    {
        breadcrumbsTruncated = false;
        contextsTruncated = false;
        return [];
    }

    private static D3D12PageFaultAllocationReport[] ReadFailedAllocations(
        out bool truncated)
    {
        truncated = false;
        return [];
    }

    private static D3D12BreadcrumbReport[] ReadBreadcrumbs(
        AutoBreadcrumbNode1* head,
        out bool breadcrumbsTruncated,
        out bool contextsTruncated)
    {
        const int maximumNodes = 256;
        var result = new List<D3D12BreadcrumbReport>();
        AutoBreadcrumbNode1* current = head;
        contextsTruncated = false;
        for (int nodeIndex = 0; current is not null && nodeIndex < maximumNodes; nodeIndex++)
        {
            uint completed = current->PLastBreadcrumbValue is null
                ? 0
                : Math.Min(*current->PLastBreadcrumbValue, current->BreadcrumbCount);
            string? operation = ReadLastBreadcrumbOperation(current, completed);
            string[] contexts = ReadBreadcrumbContexts(
                current,
                completed,
                out bool nodeContextsTruncated);
            contextsTruncated |= nodeContextsTruncated;
            result.Add(new D3D12BreadcrumbReport(
                ReadDredName(
                    current->PCommandQueueDebugNameW,
                    current->PCommandQueueDebugNameA),
                ReadDredName(
                    current->PCommandListDebugNameW,
                    current->PCommandListDebugNameA),
                completed,
                current->BreadcrumbCount,
                operation,
                ImmutableArray.CreateRange(contexts)));
            current = current->PNext;
        }
        breadcrumbsTruncated = current is not null;
        return [.. result];
    }

    private static string? ReadLastBreadcrumbOperation(
        AutoBreadcrumbNode1* node,
        uint completed)
    {
        if (completed == 0 || node->PCommandHistory is null)
            return null;
        return node->PCommandHistory[completed - 1].ToString();
    }

    private static string[] ReadBreadcrumbContexts(
        AutoBreadcrumbNode1* node,
        uint completed,
        out bool truncated)
    {
        if (node->PBreadcrumbContexts is null || node->BreadcrumbContextsCount == 0)
        {
            truncated = false;
            return [];
        }
        uint count = Math.Min(node->BreadcrumbContextsCount, 4_096u);
        truncated = node->BreadcrumbContextsCount > count;
        var contexts = new List<string>(checked((int)Math.Min(count, 16u)));
        for (uint index = 0; index < count; index++)
        {
            DredBreadcrumbContext context = node->PBreadcrumbContexts[index];
            if (context.BreadcrumbIndex > completed || context.PContextString is null)
                continue;
            string? text = Marshal.PtrToStringUni((nint)context.PContextString);
            if (!string.IsNullOrWhiteSpace(text))
                contexts.Add(text);
        }
        return [.. contexts];
    }

    private static D3D12PageFaultAllocationReport[] ReadAllocations(
        DredAllocationNode1* head,
        out bool truncated)
    {
        const int maximumNodes = 1_024;
        var result = new List<D3D12PageFaultAllocationReport>();
        DredAllocationNode1* current = head;
        for (int index = 0; current is not null && index < maximumNodes; index++)
        {
            result.Add(new D3D12PageFaultAllocationReport(
                ReadDredName(current->ObjectNameW, current->ObjectNameA),
                current->AllocationType.ToString(),
                (ulong)(nuint)current->PObject));
            current = current->PNext;
        }
        truncated = current is not null;
        return [.. result];
    }

    private static string? ReadDredName(char* wide, byte* ansi)
    {
        string? result = wide is null ? null : Marshal.PtrToStringUni((nint)wide);
        if (string.IsNullOrWhiteSpace(result) && ansi is not null)
            result = Marshal.PtrToStringAnsi((nint)ansi);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string FormatDredReport(
        int breadcrumbQueryResult,
        int pageFaultQueryResult,
        ulong pageFaultAddress,
        ReadOnlySpan<D3D12BreadcrumbReport> breadcrumbs,
        ReadOnlySpan<D3D12PageFaultAllocationReport> existing,
        ReadOnlySpan<D3D12PageFaultAllocationReport> freed,
        bool breadcrumbsTruncated,
        bool contextsTruncated,
        bool existingTruncated,
        bool freedTruncated)
    {
        var text = new StringBuilder();
        text.Append("DRED breadcrumb query: 0x")
            .Append(unchecked((uint)breadcrumbQueryResult).ToString("X8"))
            .AppendLine()
            .Append("DRED page-fault query: 0x")
            .Append(unchecked((uint)pageFaultQueryResult).ToString("X8"));
        if (pageFaultQueryResult >= 0)
        {
            text.AppendLine()
                .Append("DRED page-fault VA: 0x")
                .Append(pageFaultAddress.ToString("X16"));
        }
        AppendBreadcrumbs(text, breadcrumbs);
        AppendAllocations(text, "Existing allocation candidates", existing);
        AppendAllocations(text, "Recently freed allocation candidates", freed);
        AppendTruncation(text, "Breadcrumb chain", breadcrumbsTruncated);
        AppendTruncation(text, "Breadcrumb contexts", contextsTruncated);
        AppendTruncation(text, "Existing allocation candidates", existingTruncated);
        AppendTruncation(text, "Recently freed allocation candidates", freedTruncated);
        return text.ToString();
    }

    private static void AppendTruncation(
        StringBuilder text,
        string subject,
        bool truncated)
    {
        if (truncated)
            text.AppendLine().Append(subject).Append(" were truncated by the diagnostic limit.");
    }

    private static void AppendBreadcrumbs(
        StringBuilder text,
        ReadOnlySpan<D3D12BreadcrumbReport> breadcrumbs)
    {
        foreach (D3D12BreadcrumbReport breadcrumb in breadcrumbs)
        {
            text.AppendLine()
                .Append("Breadcrumb queue='").Append(breadcrumb.CommandQueue ?? "<unnamed>")
                .Append("' list='").Append(breadcrumb.CommandList ?? "<unnamed>")
                .Append("' completed=").Append(breadcrumb.CompletedBreadcrumbCount)
                .Append('/').Append(breadcrumb.TotalBreadcrumbCount);
            if (breadcrumb.LastOperation is not null)
                text.Append(" last=").Append(breadcrumb.LastOperation);
            foreach (string context in breadcrumb.Contexts)
                text.AppendLine().Append("  context: ").Append(context);
        }
    }

    private static void AppendAllocations(
        StringBuilder text,
        string heading,
        ReadOnlySpan<D3D12PageFaultAllocationReport> allocations)
    {
        if (allocations.IsEmpty)
            return;
        text.AppendLine().Append(heading).Append(':');
        foreach (D3D12PageFaultAllocationReport allocation in allocations)
        {
            text.AppendLine()
                .Append("  ").Append(allocation.AllocationType)
                .Append(" '").Append(allocation.Name ?? "<unnamed>")
                .Append("' object=0x")
                .Append(allocation.ObjectAddress.ToString("X"));
        }
    }

    internal static D3D12DeviceLossReport? GetDeviceLossReport(Device device) =>
        device is D3D12Device native
            ? native.DeviceLossReport
            : throw new ArgumentException(
                "The Device was not created by the Direct3D 12 backend.",
                nameof(device));

    private sealed partial class D3D12Device
    {
        private D3D12DeviceLossReport? _deviceLossReport;

        internal D3D12DeviceLossReport? DeviceLossReport =>
            Volatile.Read(ref _deviceLossReport);

        internal D3D12DeviceLossReport? CaptureDredReport()
        {
            D3D12DeviceLossReport? existing = Volatile.Read(ref _deviceLossReport);
            if (existing is not null || !_backend._dredEnabled || _native is null)
                return existing;
            D3D12DeviceLossReport? captured;
            try
            {
                captured = D3D12Backend.CaptureDredReport(this);
            }
            catch
            {
                return null;
            }
            if (captured is null)
                return null;
            return Interlocked.CompareExchange(
                ref _deviceLossReport,
                captured,
                null) ?? captured;
        }
    }
}
