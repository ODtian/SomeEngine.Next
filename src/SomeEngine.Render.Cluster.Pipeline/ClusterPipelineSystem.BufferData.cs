using System.Collections.Immutable;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct BufferWrite(BufferRange Range, ClusterBytes Bytes);
    private readonly record struct ShaderParameters(int Offset, int Capacity);

    private GraphBufferId CreateBufferData(ref RenderGraphEdit edit, ClusterBytes bytes,
        BufferUsages usages, string label)
    {
        var description = new BufferDesc(checked((ulong)Math.Max(bytes.Length, 4)),
            usages | BufferUsages.CopyDestination, label);
        GraphBufferId buffer = edit.CreatePersistentBuffer(description);
        GraphPassId upload = edit.AddBufferUploadPass(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Add(buffer, (description, upload));
        QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        return buffer;
    }

    private GraphBufferId CreateBufferData(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes,
        BufferUsages usages, string label) => CreateBufferData(ref edit, ClusterBytes.FromBytes(bytes), usages, label);

    private void UpdateBufferData(ref RenderGraphEdit edit, GraphBufferId buffer,
        ClusterBytes bytes, scoped ReadOnlySpan<BufferRange> changes)
    {
        var data = GraphData.BufferDescriptions[buffer];
        BufferDesc description = data.Description;
        ulong size = checked((ulong)Math.Max(bytes.Length, 4));
        if (description.Size != size)
        {
            description = description with { Size = size };
            edit.SetBufferDescription(buffer, description);
            GraphData.BufferDescriptions = GraphData.BufferDescriptions.SetItem(buffer, (description, data.UploadPass));
            GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
            QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        }
        else QueueBufferWrites(buffer, bytes, changes);
    }

    private void QueueBufferWrites(GraphBufferId buffer, ClusterBytes bytes,
        scoped ReadOnlySpan<BufferRange> changes)
    {
        var writes = GraphData.BufferWrites.GetValueOrDefault(buffer, ImmutableList<BufferWrite>.Empty);
        foreach (BufferRange range in changes)
        {
            if (range.Size == 0) continue;
            writes = writes.Add(new BufferWrite(range, bytes));
        }
        GraphData.BufferWrites = GraphData.BufferWrites.SetItem(buffer, writes);
    }

    private void RemoveBufferData(ref RenderGraphEdit edit, GraphBufferId buffer)
    {
        edit.Remove(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Remove(buffer);
        GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
    }

    private GraphBufferSlice CreateParameters(ref RenderGraphEdit edit,
        scoped ReadOnlySpan<byte> bytes, string label)
    {
        ShaderParameters parameters = AllocateParameters(ref edit, bytes);
        GraphBufferSlice input = new(GraphData.ShaderParameterBytes,
            new BufferRange(checked((ulong)parameters.Offset), checked((ulong)parameters.Capacity)));
        return input;
    }

    private ShaderParameters AllocateParameters(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes)
    {
        int alignment = checked((int)Math.Max(1u, _device.Capabilities.Limits.ConstantBufferAlignment));
        int capacity = checked((bytes.Length + alignment - 1) / alignment * alignment);
        int offset;
        if (GraphData.ParameterFreeOffsets.TryGetValue(capacity, out var available) && !available.IsEmpty)
        {
            offset = available.Peek();
            GraphData.ParameterFreeOffsets = GraphData.ParameterFreeOffsets.SetItem(capacity, available.Pop());
        }
        else
        {
            offset = GraphData.ParameterByteCount;
            GraphData.ParameterByteCount = checked(offset + capacity);
            var description = new BufferDesc(checked((ulong)GraphData.ParameterByteCount),
                BufferUsages.Constant, "Cluster shader parameters");
            if (GraphData.ShaderParameterBytes == default)
                GraphData.ShaderParameterBytes = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            else edit.SetBufferDescription(GraphData.ShaderParameterBytes, description, MemoryType.Upload);
        }
        var parameters = new ShaderParameters(offset, capacity);
        WriteParameterData(parameters, bytes);
        return parameters;
    }

    private void UpdateParameters(ref RenderGraphEdit edit, GraphBufferSlice input, scoped ReadOnlySpan<byte> bytes)
    {
        ShaderParameters parameters = new(checked((int)input.Range.Offset), checked((int)input.Range.Size));
        DevelopmentCheck.Require(bytes.Length <= parameters.Capacity,
            "Each shader parameter record keeps the size of its declared type.");
        WriteParameterData(parameters, bytes);
    }

    private void WriteParameterData(ShaderParameters parameters, scoped ReadOnlySpan<byte> bytes)
    {
        byte[] record = new byte[parameters.Capacity];
        bytes.CopyTo(record);
        GraphData.ParameterData = GraphData.ParameterData.WithChanges(GraphData.ParameterByteCount,
            [(parameters.Offset, record)]);
    }

    private void ReleaseParameters(ShaderParameters parameters) => GraphData.ParameterFreeOffsets =
        GraphData.ParameterFreeOffsets.SetItem(parameters.Capacity,
            GraphData.ParameterFreeOffsets.GetValueOrDefault(parameters.Capacity, ImmutableStack<int>.Empty).Push(parameters.Offset));

    private void RemoveParameters(GraphBufferSlice input)
    {
        ReleaseParameters(new(checked((int)input.Range.Offset), checked((int)input.Range.Size)));
    }
}
