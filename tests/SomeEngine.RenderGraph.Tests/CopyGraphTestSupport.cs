using SomeEngine.RenderGraph.Diagnostics;

namespace SomeEngine.RenderGraph.Tests;

internal static class CopyGraphTestSupport
{
    internal const int ByteCount = 256;

    internal static RenderGraph BuildCopyGraph(
        WarpGraphTestSupport support,
        Buffer upload,
        Buffer readback)
    {
        var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        GraphBufferId uploadId = edit.RegisterExternalBuffer(upload,
            [Endpoint(upload, support.CopyQueue, ResourceContentState.Defined)]);
        GraphBufferId readbackId = edit.RegisterExternalBuffer(readback,
            [Endpoint(readback, support.GraphicsQueue, ResourceContentState.Undefined)]);
        GraphBufferId transient = edit.CreateTransientBuffer(new BufferDesc(
            ByteCount,
            BufferUsages.CopySource | BufferUsages.CopyDestination));
        _ = AddCopy(edit, "Upload", support.CopyQueue,
            new CopyState(uploadId, transient, ByteCount));
        _ = AddCopy(edit, "Readback", support.GraphicsQueue,
            new CopyState(transient, readbackId, ByteCount));
        edit.Commit();
        return graph;
    }

    internal static GraphPassId AddCopy(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        in CopyState state) =>
        edit.DeclareBufferCopy(label, PassQueueSelection.Exact(queue),
            new GraphBufferSlice(state.Source, new BufferRange(0, state.Size)),
            new GraphBufferSlice(state.Destination, new BufferRange(0, state.Size)));

    internal static GraphPassId AddClear(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        GraphBufferId buffer,
        uint value)
    {
        return edit.DeclareCopyPass(label, PassQueueSelection.Exact(queue),
            (ref PassDefinition access) => access.Write(buffer, new BufferRange(0, 64),
                PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
            (ref CopyPassCommandScope commands) =>
                commands.ClearBuffer(commands.GetBuffer(buffer), new BufferRange(0, 64), value),
            new PassOptions(Culling: PassCullingMode.NeverCull));
    }

    internal static GraphPassId AddRead(
        RenderGraphEdit edit,
        string label,
        Queue queue,
        GraphBufferId buffer) =>
        edit.DeclareCopyPass(label, PassQueueSelection.Exact(queue),
            (ref PassDefinition access) => access.Read(buffer, new BufferRange(0, 64),
                PipelineSync.Copy, ResourceAccess.CopySource),
            options: new PassOptions(Culling: PassCullingMode.NeverCull));

    internal static void DeclareCopy(ref PassDefinition access, ref CopyState state)
    {
        _ = access.Read(state.Source, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopySource);
        _ = access.Write(state.Destination, new BufferRange(0, state.Size),
            PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
    }

    internal static void RecordCopy(ref CopyPassCommandScope commands, in CopyState state)
    {
        Buffer source = commands.GetBuffer(state.Source);
        Buffer destination = commands.GetBuffer(state.Destination);
        commands.CopyBuffer(new BufferCopy(source, 0, destination, 0, state.Size));
    }

    internal static BufferBoundaryState Endpoint(
        Buffer buffer,
        Queue queue,
        ResourceContentState contents) =>
        new(new BufferRange(0, buffer.Info.Size),
            buffer.InitialSync, buffer.InitialAccess, contents, queue);

    internal static RenderGraphSnapshot CaptureSnapshot(in RenderGraphDiagnosticsView view)
    {
        return RenderGraphSnapshot.Capture(view);
    }

    internal readonly record struct CopyState(
        GraphBufferId Source,
        GraphBufferId Destination,
        ulong Size);

    internal readonly record struct DependentClearState(
        GraphBufferId Gate,
        GraphBufferId Buffer,
        uint Value);

    internal readonly record struct ObservationState(int[] Destination);

    internal readonly record struct DynamicCopyFrame(ulong Offset, ulong Size);

    internal readonly record struct TextureWriteState(GraphTextureId Texture);

    internal readonly record struct DynamicTextureWriteFrame(TextureSubresourceRange Range);
}
