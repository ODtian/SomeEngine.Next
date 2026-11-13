using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Elements.Render;

/// <summary>Canvas instance bytes and the graph buffer used by their material views.</summary>
internal sealed class InstanceBuffer(GraphBufferId buffer, GraphPassId upload, byte[] bytes, BufferSrv[] views)
    : IDisposable, IRenderGraphFrameExecutionObserver
{
    private byte[] _bytes = bytes;
    private ulong _version = 1;
    private ulong _uploadedVersion;
    internal GraphBufferId Buffer { get; } = buffer;
    internal GraphPassId UploadPass { get; } = upload;
    internal BufferSrv[] Views { get; } = views;

    internal void Upload(ref RenderGraphFrame frame)
    {
        if (_uploadedVersion == _version) return;
        frame.WriteBuffer(Buffer, 0, _bytes);
        frame.ObserveExecution(this);
    }

    public void OnFrameSubmitted(in RenderGraphSubmission submission) => _uploadedVersion = _version;

    internal static InstanceBuffer Create(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, byte[] bytes, int count, int stride, string label, InstanceBuffer? previous = null)
    {
        if (previous is not null && previous._bytes.Length == bytes.Length && previous.Views.Length == count)
        {
            previous._bytes = bytes;
            previous._version++;
            return previous;
        }
        GraphBufferId buffer;
        GraphPassId upload;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreatePersistentBuffer(new BufferDesc((ulong)bytes.Length,
                BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination, label));
            upload = edit.AddBufferUploadPass(buffer);
            edit.Commit();
        }
        SomeEngine.Graphics.Buffer resource = graph.GetBuffer(buffer);
        var views = new BufferSrv[count];
        for (int index = 0; index < count; index++)
        {
            ulong offset = (ulong)index * (ulong)stride;
            views[index] = backend.CreateBufferSrv(device, new BufferSrvDesc(resource,
                new BufferRange(offset, resource.Info.Size - offset), Label: "Canvas instance range"));
        }
        return new(buffer, upload, bytes, views);
    }

    public void Dispose()
    {
        foreach (BufferSrv view in Views) view.Dispose();
        _bytes = [];
    }
}
