using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class MemoryBudgetTests
{
    [Fact]
    public void Warp_reports_dxgi_budget_usage_and_resource_residency()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP memory-budget lane must execute on Windows.");
        using Device device = new(new Options { UseWarpAdapter = true, EnableDebugLayer = true });

        MemoryBudget budget = device.GetMemoryBudget(MemoryType.DeviceLocal);
        Assert.True(budget.Budget > 0);
        Assert.Equal(budget.Usage >= budget.Budget ? 0ul : budget.Budget - budget.Usage, budget.Available);

        BufferHandle buffer = device.CreateBuffer(new BufferDesc(
            4096,
            BufferUsage.CopySource | BufferUsage.CopyDestination));
        ResourceMemoryInfo initial = device.GetResourceMemoryInfo(buffer.Resource);
        Assert.Equal(buffer.Resource, initial.Resource);
        Assert.Equal(MemoryType.DeviceLocal, initial.MemoryType);
        Assert.True(initial.Size >= 4096);
        Assert.True(initial.Resident);
        Assert.Equal(ResidencyPriority.Normal, initial.Priority);

        device.SetResidencyPriority(buffer.Resource, ResidencyPriority.High);
        Assert.Equal(ResidencyPriority.High, device.GetResourceMemoryInfo(buffer.Resource).Priority);

        device.DestroyBuffer(buffer);
        device.CollectGarbage();
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }
}
