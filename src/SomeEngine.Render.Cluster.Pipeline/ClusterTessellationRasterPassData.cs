using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

internal readonly record struct ClusterTessellationRasterPassData(
    IndirectCommandLayout IndirectLayout,
    GraphBufferId IndirectArguments,
    ulong IndirectOffset,
    GraphBufferId Indices,
    GraphBufferId PatchIndices,
    int Width,
    int Height,
    bool UsesMesh)
{
    internal static void Declare(ref PassDefinition pass, ref ClusterTessellationRasterPassData data)
    {
        pass.Read(data.IndirectArguments, new BufferRange(data.IndirectOffset, data.UsesMesh ? ClusterIndirectAbi.DispatchBytes : 8u * ClusterIndirectAbi.DrawIndexedBytes),
            PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        if (data.UsesMesh) return;
        pass.Read(data.Indices, BufferRange.Whole, PipelineSync.IndexInput, ResourceAccess.IndexBuffer);
        pass.Read(data.PatchIndices, BufferRange.Whole, PipelineSync.VertexShading, ResourceAccess.VertexBuffer,
            contentScope: BufferContentScope.CountBoundedPrefix);
    }

    internal static void Record(ref RasterPassCommandScope commands, in ClusterTessellationRasterPassData data)
    {
        commands.SetViewports([new Viewport(0, 0, data.Width, data.Height)]);
        commands.SetScissors([new ScissorRect(0, 0, data.Width, data.Height)]);
        if (data.UsesMesh)
        {
            commands.DispatchMeshIndirect(new BufferRegion(commands.GetBuffer(data.IndirectArguments),
                new BufferRange(data.IndirectOffset, ClusterIndirectAbi.DispatchBytes)));
            return;
        }
        Buffer indices = commands.GetBuffer(data.Indices);
        commands.SetIndexBuffer(new IndexBufferBinding(indices, 0, indices.Info.Size, IndexType.UInt16));
        Buffer patches = commands.GetBuffer(data.PatchIndices);
        commands.SetVertexBuffers(0, [new VertexBufferBinding(patches, 0, sizeof(uint), patches.Info.Size)]);
        commands.ExecuteIndirect(data.IndirectLayout,
            new BufferRegion(commands.GetBuffer(data.IndirectArguments),
                new BufferRange(data.IndirectOffset, data.UsesMesh ? ClusterIndirectAbi.DispatchBytes : 8u * ClusterIndirectAbi.DrawIndexedBytes)), 8);
    }
}
