using System.Reflection;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph.Diagnostics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RenderGraphRhiTests
{
    [Fact]
    public void Cross_queue_graph_emits_handoffs_and_round_trips_native_memory()
    {
        Assert.True(OperatingSystem.IsWindows());
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = WarpGraphTestSupport.CreateDevice(backend);
        byte[] expected = CreatePattern(1024);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(1024, BufferUsages.CopyDestination, "graph readback"),
            MemoryType.Readback);

        using var graph = new RenderGraph(backend, device);
        BufferHandle upload = graph.CreateUploadBuffer(expected, "graph upload");
        BufferHandle transient = graph.CreateBuffer(new BufferDesc(
            1024,
            BufferUsages.CopySource | BufferUsages.CopyDestination,
            "graph transient"));
        BufferHandle destination = graph.Import(
            readback,
            GraphResourceUsage.CopyDestination,
            GraphResourceUsage.CopyDestination,
            contentsAvailable: false);
        RecordCopy(graph, "copy upload on copy queue", upload, transient, QueueType.Copy);
        RecordCopy(graph, "copy result on graphics queue", transient, destination, QueueType.Graphics);

        QueueCompletion[] completions =
            RenderGraphDiagnostics.ExecuteWithSnapshot(graph, out RenderGraphSnapshot snapshot);
        WarpGraphTestSupport.WaitAll(backend, completions);

        byte[] actual = new byte[expected.Length];
        BufferRange range = new(0, checked((ulong)actual.Length));
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        backend.CollectCompleted(device);

        Assert.Equal(expected, actual);
        Assert.True(snapshot.Succeeded);
        Assert.Equal(RenderGraphSnapshot.CurrentVersion, snapshot.Version);
        Assert.Contains(
            snapshot.Barriers,
            static barrier => barrier.Kind == RenderGraphSnapshot.BarrierKind.QueueRelease);
        Assert.Contains(
            snapshot.Barriers,
            static barrier => barrier.Kind == RenderGraphSnapshot.BarrierKind.QueueAcquire);
        Assert.Contains(snapshot.Batches, static batch => batch.Queue == QueueType.Copy);
        Assert.Contains(snapshot.Batches, static batch => batch.Queue == QueueType.Graphics);
    }

    [Fact]
    public void Handles_are_scoped_to_one_graph_invocation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = WarpGraphTestSupport.CreateDevice(backend);
        using var first = new RenderGraph(backend, device);
        using var second = new RenderGraph(backend, device);
        BufferHandle foreign = first.CreateBuffer(new BufferDesc(
            16,
            BufferUsages.CopySource,
            "foreign"));

        using IUnsafeRenderGraphBuilder builder = second.AddUnsafePass<CopyPassData>(
            "reject foreign handle",
            out _,
            QueueType.Copy);

        Assert.Throws<ArgumentException>(() =>
            builder.UseBuffer(foreign, GraphResourceUsage.CopySource));
    }

    [Fact]
    public void Non_overlapping_transients_alias_with_an_explicit_native_barrier()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = WarpGraphTestSupport.CreateDevice(backend);
        byte[] expected = CreatePattern(1024);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(1024, BufferUsages.CopyDestination, "aliasing readback"),
            MemoryType.Readback);

        using var graph = new RenderGraph(backend, device);
        BufferHandle upload = graph.CreateUploadBuffer(expected, "aliasing upload");
        BufferDesc transientDescription = new(
            1024,
            BufferUsages.CopySource | BufferUsages.CopyDestination,
            "aliasable transient");
        BufferHandle first = graph.CreateBuffer(transientDescription with { Label = "alias first" });
        BufferHandle bridge = graph.CreateBuffer(transientDescription with { Label = "alias bridge" });
        BufferHandle second = graph.CreateBuffer(transientDescription with { Label = "alias second" });
        BufferHandle destination = graph.Import(
            readback,
            GraphResourceUsage.CopyDestination,
            GraphResourceUsage.CopyDestination,
            contentsAvailable: false);
        RecordCopy(graph, "initialize first alias", upload, first, QueueType.Copy);
        RecordCopy(graph, "retire first alias", first, bridge, QueueType.Copy);
        RecordCopy(graph, "activate second alias", bridge, second, QueueType.Copy);
        RecordCopy(graph, "read second alias", second, destination, QueueType.Copy);

        QueueCompletion[] completions =
            RenderGraphDiagnostics.ExecuteWithSnapshot(graph, out RenderGraphSnapshot snapshot);
        WarpGraphTestSupport.WaitAll(backend, completions);
        byte[] actual = new byte[expected.Length];
        BufferRange range = new(0, checked((ulong)actual.Length));
        using (MappedBuffer mapping = backend.Map(readback, MapType.Read, range))
        {
            mapping.Invalidate(range);
            mapping.Bytes.CopyTo(actual);
        }
        backend.CollectCompleted(device);

        Assert.Equal(expected, actual);
        Assert.Contains(
            snapshot.Barriers,
            static barrier => barrier.Kind == RenderGraphSnapshot.BarrierKind.Aliasing);
    }

    [Fact]
    public void Public_render_graph_data_does_not_capture_a_backend_type_parameter()
    {
        Type[] exported = typeof(RenderGraph).Assembly.GetExportedTypes();

        Assert.DoesNotContain(
            exported.SelectMany(static type => type.GetGenericArguments()),
            static parameter => parameter.Name.Contains("Backend", StringComparison.Ordinal));
        Assert.DoesNotContain(
            exported.SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Instance)),
            static field => ContainsBackendTypeParameter(field.FieldType));
        Assert.DoesNotContain(
            exported.SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)),
            static property => ContainsBackendTypeParameter(property.PropertyType));
    }

    private static bool ContainsBackendTypeParameter(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name.Contains("Backend", StringComparison.Ordinal);
        return type.IsGenericType &&
            type.GetGenericArguments().Any(ContainsBackendTypeParameter);
    }

    private static void RecordCopy(
        RenderGraph graph,
        string name,
        BufferHandle source,
        BufferHandle destination,
        QueueType queue)
    {
        using IUnsafeRenderGraphBuilder builder = graph.AddUnsafePass<CopyPassData>(
            name,
            out CopyPassData data,
            queue);
        data.Source = source;
        data.Destination = destination;
        builder.UseBuffer(source, GraphResourceUsage.CopySource, GraphAccess.Read);
        builder.UseBuffer(destination, GraphResourceUsage.CopyDestination, GraphAccess.WriteAll);
        builder.SetRenderFunc<CopyPassData>(static (pass, context) =>
            context.CopyBufferRegion(pass.Source, 0, pass.Destination, 0, 1024));
    }

    private static byte[] CreatePattern(int length)
    {
        var result = new byte[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = unchecked((byte)(91 + index * 29));
        return result;
    }

    private sealed class CopyPassData
    {
        public BufferHandle Source;
        public BufferHandle Destination;
    }
}
