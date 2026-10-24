using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MeshOptimizer;
using SharpGLTF.Schema2;

namespace SomeEngine.Assets.Importers;

/// <summary>Reads source components directly so sparse data and unsigned integers keep their precision.</summary>
internal sealed class GltfAccessorStreams(ModelRoot source, JsonNode document)
{
    private readonly Dictionary<int, byte[]> _decodedViews = [];

    public VertexStream Read(string name, Accessor accessor, bool morph = false)
    {
        JsonNode description = document["accessors"]![accessor.LogicalIndex]!;
        string shape = description["type"]!.GetValue<string>();
        (int rows, int columns) = shape switch
        {
            "SCALAR" => (1, 1),
            "VEC2" => (2, 1),
            "VEC3" => (3, 1),
            "VEC4" => (4, 1),
            "MAT2" => (2, 2),
            "MAT3" => (3, 3),
            "MAT4" => (4, 4),
            _ => throw new NotSupportedException($"Accessor shape {shape} is unsupported."),
        };
        int components = rows * columns;
        double[] values = new double[checked(accessor.Count * components)];
        int componentBytes = GetComponentSize(accessor.Encoding);
        int columnBytes = columns == 1 ? rows * componentBytes : (rows * componentBytes + 3) & ~3;
        int itemBytes = columnBytes * columns;
        if (description["bufferView"] is JsonNode viewIndex)
        {
            int view = viewIndex.GetValue<int>();
            int stride = document["bufferViews"]![view]!["byteStride"]?.GetValue<int>() ?? itemBytes;
            ReadItems(values, ReadViewBytes(view), accessor.ByteOffset, stride, accessor.Count,
                accessor.Encoding, accessor.Normalized, rows, columns, columnBytes);
        }
        if (description["sparse"] is JsonNode sparse)
        {
            JsonNode indices = sparse["indices"]!, replacements = sparse["values"]!;
            EncodingType indexType = (EncodingType)indices["componentType"]!.GetValue<int>();
            int indexBytes = GetComponentSize(indexType);
            ReadOnlySpan<byte> indexData = ReadViewBytes(indices["bufferView"]!.GetValue<int>()).Span;
            int indexOffset = indices["byteOffset"]?.GetValue<int>() ?? 0;
            int count = sparse["count"]!.GetValue<int>();
            double[] replacementValues = new double[checked(count * components)];
            ReadItems(replacementValues, ReadViewBytes(replacements["bufferView"]!.GetValue<int>()),
                replacements["byteOffset"]?.GetValue<int>() ?? 0, itemBytes, count,
                accessor.Encoding, accessor.Normalized, rows, columns, columnBytes);
            for (int index = 0; index < count; index++)
            {
                int vertex = checked((int)ReadComponent(indexData.Slice(indexOffset + index * indexBytes, indexBytes), indexType, false));
                replacementValues.AsSpan(index * components, components).CopyTo(values.AsSpan(vertex * components, components));
            }
        }
        if (name.StartsWith("COLOR_", StringComparison.Ordinal) && components == 3)
        {
            double[] rgba = new double[checked(accessor.Count * 4)];
            for (int vertex = 0; vertex < accessor.Count; vertex++)
            {
                values.AsSpan(vertex * 3, 3).CopyTo(rgba.AsSpan(vertex * 4, 3));
                rgba[vertex * 4 + 3] = morph ? 0 : 1;
            }
            return new(4, rgba);
        }
        return new(components, values);
    }

    private ReadOnlyMemory<byte> ReadViewBytes(int index)
    {
        JsonNode view = document["bufferViews"]![index]!;
        JsonNode? extension = view["extensions"]?["EXT_meshopt_compression"] ?? view["extensions"]?["KHR_meshopt_compression"];
        if (extension is null) return source.LogicalBufferViews[index].Content;
        if (_decodedViews.TryGetValue(index, out byte[]? decoded)) return decoded;
        int count = extension["count"]!.GetValue<int>(), stride = extension["byteStride"]!.GetValue<int>();
        byte[] compressed = source.LogicalBuffers[extension["buffer"]!.GetValue<int>()].Content;
        int offset = extension["byteOffset"]?.GetValue<int>() ?? 0;
        int length = extension["byteLength"]!.GetValue<int>();
        decoded = DecodeMeshopt(compressed.AsSpan(offset, length), count, stride,
            extension["mode"]!.GetValue<string>(), extension["filter"]?.GetValue<string>() ?? "NONE");
        _decodedViews.Add(index, decoded);
        return decoded;
    }

    private static unsafe byte[] DecodeMeshopt(ReadOnlySpan<byte> compressed, int count, int stride, string mode, string filter)
    {
        // These limits protect the native decoder while consuming external source data.
        if (count <= 0 || stride <= 0 || (mode == "ATTRIBUTES" && (stride > 256 || stride % 4 != 0))
            || (mode is "TRIANGLES" or "INDICES" && stride is not (2 or 4))
            || (mode == "TRIANGLES" && count % 3 != 0))
            throw new InvalidDataException("meshopt element count or byte stride is invalid.");
        if ((filter == "OCTAHEDRAL" && stride is not (4 or 8)) || (filter == "QUATERNION" && stride != 8)
            || (filter == "EXPONENTIAL" && stride % 4 != 0) || (filter == "COLOR" && stride is not (4 or 8)))
            throw new InvalidDataException($"meshopt {filter} filter byte stride is invalid.");
        byte[] decoded = new byte[checked(count * stride)];
        fixed (byte* destination = decoded)
        fixed (byte* input = compressed)
        {
            int result = mode switch
            {
                "ATTRIBUTES" => Meshopt.DecodeVertexBuffer(destination, (nuint)count, (nuint)stride, input, (nuint)compressed.Length),
                "TRIANGLES" => Meshopt.DecodeIndexBuffer(destination, (nuint)count, (nuint)stride, input, (nuint)compressed.Length),
                "INDICES" => Meshopt.DecodeIndexSequence(destination, (nuint)count, (nuint)stride, input, (nuint)compressed.Length),
                _ => throw new NotSupportedException($"meshopt mode {mode} is unsupported."),
            };
            if (result != 0) throw new InvalidDataException($"meshopt {mode} decompression failed ({result}).");
            switch (filter)
            {
                case "NONE": break;
                case "OCTAHEDRAL": Meshopt.DecodeFilterOct(destination, (nuint)count, (nuint)stride); break;
                case "QUATERNION": Meshopt.DecodeFilterQuat(destination, (nuint)count, (nuint)stride); break;
                case "EXPONENTIAL": Meshopt.DecodeFilterExp(destination, (nuint)count, (nuint)stride); break;
                case "COLOR": Meshopt.DecodeFilterColor(destination, (nuint)count, (nuint)stride); break;
                default: throw new NotSupportedException($"meshopt filter {filter} is unsupported.");
            }
        }
        return decoded;
    }

    private static void ReadItems(double[] values, ReadOnlyMemory<byte> bytes, int offset, int stride, int count,
        EncodingType encoding, bool normalized, int rows, int columns, int columnBytes)
    {
        int size = GetComponentSize(encoding), components = rows * columns;
        for (int vertex = 0; vertex < count; vertex++)
            for (int column = 0; column < columns; column++)
                for (int row = 0; row < rows; row++)
                    values[vertex * components + column * rows + row] = ReadComponent(
                        bytes.Span.Slice(offset + vertex * stride + column * columnBytes + row * size, size), encoding, normalized);
    }

    private static int GetComponentSize(EncodingType encoding) => encoding switch
    {
        EncodingType.BYTE or EncodingType.UNSIGNED_BYTE => 1,
        EncodingType.SHORT or EncodingType.UNSIGNED_SHORT => 2,
        EncodingType.UNSIGNED_INT or EncodingType.FLOAT => 4,
        _ => throw new NotSupportedException($"Accessor component type {encoding} is unsupported."),
    };

    private static double ReadComponent(ReadOnlySpan<byte> bytes, EncodingType encoding, bool normalized)
    {
        double value = encoding switch
        {
            EncodingType.BYTE => (sbyte)bytes[0],
            EncodingType.UNSIGNED_BYTE => bytes[0],
            EncodingType.SHORT => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            EncodingType.UNSIGNED_SHORT => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            EncodingType.UNSIGNED_INT => (double)BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            EncodingType.FLOAT => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            _ => throw new NotSupportedException($"Accessor component type {encoding} is unsupported."),
        };
        if (!normalized) return value;
        return encoding switch
        {
            EncodingType.BYTE => Math.Max((float)value / 127f, -1f),
            EncodingType.UNSIGNED_BYTE => (float)value / 255f,
            EncodingType.SHORT => Math.Max((float)value / 32767f, -1f),
            EncodingType.UNSIGNED_SHORT => (float)value / 65535f,
            _ => value,
        };
    }
}
