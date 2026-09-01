using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features;

internal static class FeatureUploads
{
    private const int ConstantBufferAlignment = 256;

    internal static GraphBufferId UploadConstant<T>(
        ref RenderGraphFrame graph,
        in T value,
        string label)
        where T : unmanaged
    {
        if (Marshal.SizeOf<T>() > ConstantBufferAlignment)
            throw new ArgumentOutOfRangeException(nameof(value), "Feature constant data exceeds one aligned CBV block.");
        Span<byte> bytes = stackalloc byte[ConstantBufferAlignment];
        bytes.Clear();
        MemoryMarshal.Write(bytes, in value);
        return graph.Upload(bytes, BufferUsages.Constant, label);
    }
}
