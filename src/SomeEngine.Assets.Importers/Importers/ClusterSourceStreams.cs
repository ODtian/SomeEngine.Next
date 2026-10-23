using System.Buffers.Binary;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

internal static class ClusterSourceStreams
{
    internal static ClusterVertexAttribute Read(string name, ClusterVertexSource[] rows,
        Func<ClusterVertexSource, VertexStream?> source)
    {
        VertexStream?[] streams = rows.Select(source).ToArray();
        int components = streams.OfType<VertexStream>().Max(stream => stream.ComponentCount);
        byte[] data = new byte[rows.Length * components * sizeof(double)];
        int[] counts = new int[rows.Length];
        for (int vertex = 0; vertex < rows.Length; vertex++)
        {
            VertexStream? stream = streams[vertex];
            if (stream is null) continue;
            counts[vertex] = stream.ComponentCount;
            for (int component = 0; component < stream.ComponentCount; component++)
                BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan((vertex * components + component) * sizeof(double)),
                    stream.Values.Span[rows[vertex].Vertex * stream.ComponentCount + component]);
        }
        return new() { Name = name, ComponentCount = components, VertexComponentCounts = [.. counts], Data = [.. data] };
    }
}
