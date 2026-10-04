using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Elements.Render;

/// <summary>Material instance bytes with sampled buffer ranges starting at each instance.</summary>
internal sealed class InstanceBuffer(Buffer buffer, BufferSrv[] views) : IDisposable
{
    internal Buffer Buffer { get; } = buffer;
    internal BufferSrv[] Views { get; } = views;
    internal BufferBoundaryState[] Boundaries { get; set; } =
        [new(BufferRange.Whole, buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Defined)];

    internal static InstanceBuffer Create(IGraphicsBackend backend, Device device, Buffer buffer, int count, int instanceByteCount)
    {
        var views = new BufferSrv[count];
        try
        {
            for (int index = 0; index < count; index++)
            {
                ulong offset = (ulong)index * (ulong)instanceByteCount;
                views[index] = backend.CreateBufferSrv(device, new BufferSrvDesc(buffer,
                    new BufferRange(offset, buffer.Info.Size - offset), Label: "Canvas instance range"));
            }
            return new(buffer, views);
        }
        catch
        {
            foreach (BufferSrv? view in views) view?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (BufferSrv view in Views) view.Dispose();
        Buffer.Dispose();
    }
}
