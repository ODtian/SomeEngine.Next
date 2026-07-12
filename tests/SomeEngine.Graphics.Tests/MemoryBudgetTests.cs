using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class MemoryBudgetTests
{
    [Fact]
    public void Null_reports_deterministic_budget_and_residency_transitions()
    {
        using var device = new Device(new Options { UploadBudget = 4096 });
        MemoryBudget before = device.GetMemoryBudget(MemoryType.Upload);
        Assert.Equal(4096UL, before.Budget);
        Assert.Equal(0UL, before.Usage);

        BufferHandle buffer = device.CreateBuffer(
            new BufferDesc(300, BufferUsage.CopySource),
            MemoryType.Upload);
        MemoryBudget allocated = device.GetMemoryBudget(MemoryType.Upload);
        ResourceMemoryInfo info = device.GetResourceMemoryInfo(buffer.Resource);
        Assert.Equal(512UL, allocated.Usage);
        Assert.Equal(allocated.Budget - allocated.Usage, allocated.Available);
        Assert.Equal(ResidencyPriority.Normal, info.Priority);
        Assert.True(info.Resident);
        Assert.Equal(MemoryType.Upload, info.MemoryType);

        device.SetResidencyPriority(buffer.Resource, ResidencyPriority.Critical);
        Assert.Equal(ResidencyPriority.Critical, device.GetResourceMemoryInfo(buffer.Resource).Priority);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.SetResidencyPriority(buffer.Resource, (ResidencyPriority)byte.MaxValue));

        device.DestroyBuffer(buffer);
        Assert.Equal(1, device.CollectGarbage());
        Assert.Equal(0UL, device.GetMemoryBudget(MemoryType.Upload).Usage);
    }
}
