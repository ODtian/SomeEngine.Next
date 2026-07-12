using System.Collections;
using System.Reflection;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using D3DDevice = SomeEngine.Graphics.Direct3D12.Device;
using D3DOptions = SomeEngine.Graphics.Direct3D12.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ResourceRealizationTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Warp_retires_realized_resources_only_after_exact_completion()
    {
        const int byteCount = 16 * 1024 * 1024;
        using D3DDevice device = new(new D3DOptions { UseWarpAdapter = true, EnableDebugLayer = true });
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc((ulong)byteCount, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc((ulong)byteCount, BufferUsage.CopyDestination),
            MemoryType.Readback);
        byte[] expected = new byte[byteCount];
        for (int index = 0; index < expected.Length; index += 4096)
            expected[index] = unchecked((byte)(index / 4096 * 29 + 7));
        device.WriteBuffer(upload, 0, expected);
        BufferHandle realized = default;
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId destination = builder.ImportBuffer(
                readback,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            BufferId transient = builder.CreateBuffer(new BufferDesc(
                (ulong)byteCount,
                BufferUsage.CopySource | BufferUsage.CopyDestination,
                "completion-gated-transient"));

            PassBuilder produce = builder.AddPass("realize-transient", QueueSelection.Copy);
            BufferAccess uploadAccess = produce.Read(source, BufferUse.CopySource);
            BufferAccess transientWrite = produce.Write(transient, BufferUse.CopyDestination);
            produce.Execute((ICommandContext commands, in PassResources resources) =>
            {
                realized = resources.Get(transientWrite);
                commands.CopyBuffer(
                    resources.Get(uploadAccess),
                    0,
                    realized,
                    0,
                    (ulong)byteCount);
            });

            PassBuilder consume = builder.AddPass("retire-after-copy", QueueSelection.Copy);
            BufferAccess transientRead = consume.Read(transient, BufferUse.CopySource);
            BufferAccess readbackWrite = consume.Write(destination, BufferUse.CopyDestination);
            consume.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(
                    resources.Get(transientRead),
                    0,
                    resources.Get(readbackWrite),
                    0,
                    (ulong)byteCount));

            GraphExecution execution = graph.Execute(ref builder);
            GpuCompletion completion = Assert.Single(execution.Completions);
            Assert.True(realized.IsValid);
            Assert.Throws<ArgumentException>(() => device.GetBufferMetadata(realized));
            RetirementSnapshot retirement = FindRetirement(device, "completion-gated-transient");
            Assert.Equal(0UL, retirement.Graphics);
            Assert.Equal(0UL, retirement.Compute);
            Assert.Equal(completion.Value, retirement.Copy);

            if (device.GetCompletedValue(completion.Queue) < completion.Value)
                Assert.Equal(0, device.CollectGarbage());

            Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(device.GetCompletedValue(completion.Queue) >= completion.Value);
            Assert.True(device.CollectGarbage() >= 1);

            byte[] actual = new byte[byteCount];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
                diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    private static RetirementSnapshot FindRetirement(D3DDevice device, string resourceName)
    {
        FieldInfo field = typeof(D3DDevice).GetField(
            "_retiredObjects",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("D3D12 retirement storage was not found.");
        IEnumerable values = (IEnumerable)(field.GetValue(device)
            ?? throw new Xunit.Sdk.XunitException("D3D12 retirement storage is unavailable."));
        foreach (object entry in values)
        {
            Type entryType = entry.GetType();
            object native = entryType.GetProperty("Value")!.GetValue(entry)!;
            if (!string.Equals(native.GetType().Name, "NativeBuffer", StringComparison.Ordinal)) continue;
            object description = native.GetType().GetProperty("Desc")!.GetValue(native)!;
            string? name = (string?)description.GetType().GetProperty(nameof(BufferDesc.Name))!.GetValue(description);
            if (!string.Equals(name, resourceName, StringComparison.Ordinal)) continue;
            object point = entryType.GetProperty("Point")!.GetValue(entry)!;
            Type pointType = point.GetType();
            return new RetirementSnapshot(
                (ulong)pointType.GetProperty("Graphics")!.GetValue(point)!,
                (ulong)pointType.GetProperty("Compute")!.GetValue(point)!,
                (ulong)pointType.GetProperty("Copy")!.GetValue(point)!);
        }
        throw new Xunit.Sdk.XunitException($"Retirement for native resource '{resourceName}' was not scheduled.");
    }

    private readonly record struct RetirementSnapshot(ulong Graphics, ulong Compute, ulong Copy);
}
