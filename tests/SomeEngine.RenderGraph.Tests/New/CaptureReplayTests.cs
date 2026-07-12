using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CaptureReplayTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Capture_json_and_dot_are_schema_versioned_and_deterministic()
    {
        using NullDevice device = new();
        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        device.WriteBuffer(upload, 0, new byte[] { 8, 6, 7, 5 });
        try
        {
            using RenderGraph cached = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
                EnableCapture = true,
            });
            Capture first = ExecuteCopy(cached, upload, readback).Capture!;
            Capture second = ExecuteCopy(cached, upload, readback).Capture!;

            using RenderGraph uncached = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
                EnableCapture = true,
                CompilationCacheEntryLimit = 0,
                CompilationCachePayloadByteBudget = 0,
            });
            Capture withoutCache = ExecuteCopy(uncached, upload, readback).Capture!;

            Assert.Equal(Capture.CurrentSchemaVersion, first.SchemaVersion);
            Assert.Equal(first.ToJson(indented: false), second.ToJson(indented: false));
            Assert.Equal(first.ToJson(indented: false), withoutCache.ToJson(indented: false));
            Assert.Equal(first.ToDot(), second.ToDot());
            Assert.Equal(first.ToDot(), withoutCache.ToDot());
            Assert.Equal(1, cached.Statistics.CacheHits);
            byte[] actual = new byte[4];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(new byte[] { 8, 6, 7, 5 }, actual);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
        }
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Null_replay_reproduces_compiled_structure_and_rejects_corruption()
    {
        using NullDevice device = new();
        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        device.WriteBuffer(upload, 0, new byte[] { 1, 3, 3, 7 });
        try
        {
            using RenderGraph graph = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
                EnableCapture = true,
            });
            Capture captured = ExecuteCopy(graph, upload, readback).Capture!;
            Capture restored = Capture.FromJson(captured.ToJson(indented: false));
            ReplayResult replay = ReplayExecutor.Execute(restored, device);

            Assert.Equal(captured.CanonicalSignature, replay.CanonicalSignature);
            Assert.Equal(captured.Resources.Count, replay.ResourceCount);
            Assert.Equal(restored.Batches.Count, replay.ExecutedBatchCount);
            Assert.Equal(restored.Batches.Count, replay.Completions.Count);
            Assert.All(replay.Completions, completion => Assert.True(device.Wait(completion, TimeSpan.Zero)));
            Assert.Equal(
                captured.Passes.Where(static pass => pass.Active).Select(static pass => pass.Name),
                replay.ActivePasses);

            string schemaCorruption = captured.ToJson(indented: false)
                .Replace(
                    FormattableString.Invariant($"\"schemaVersion\":{Capture.CurrentSchemaVersion}"),
                    "\"schemaVersion\":999",
                    StringComparison.Ordinal);
            Assert.Throws<NotSupportedException>(() => ReplayExecutor.Execute(Capture.FromJson(schemaCorruption), device));

            string topologyCorruption = captured.ToJson(indented: false)
                .Replace("\"dependencies\":[]", "\"dependencies\":[999]", StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => ReplayExecutor.Execute(Capture.FromJson(topologyCorruption), device));
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
        }
    }

    private static GraphExecution ExecuteCopy(RenderGraph graph, BufferHandle upload, BufferHandle readback)
    {
        GraphBuilder builder = graph.Begin();
        BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
        BufferId destination = builder.ImportBuffer(
            readback,
            BufferUse.CopyDestination,
            BufferUse.CopyDestination,
            contentsAvailable: false);
        PassBuilder pass = builder.AddPass("capture-copy", QueueSelection.Copy);
        BufferAccess input = pass.Read(source, BufferUse.CopySource, new BufferRange(0, 4));
        BufferAccess output = pass.Write(destination, BufferUse.CopyDestination, new BufferRange(0, 4));
        pass.Execute((ICommandContext commands, in PassResources resources) =>
            commands.CopyBuffer(resources.Get(input), 0, resources.Get(output), 0, 4));
        GraphExecution execution = graph.Execute(ref builder);
        Assert.True(execution.Wait(TimeSpan.Zero));
        return execution;
    }
}
