namespace SomeEngine.Graphics.Null;

internal abstract record RecordedCommand;
internal sealed record BarrierCommand(ResourceBarrier[] Barriers) : RecordedCommand;
internal sealed record CopyBufferCommand(BufferHandle Source, ulong SourceOffset, BufferHandle Destination, ulong DestinationOffset, ulong Size) : RecordedCommand;
internal sealed record CopyBufferToTextureCommand(BufferTextureCopy Copy) : RecordedCommand;
internal sealed record CopyTextureToBufferCommand(TextureBufferCopy Copy) : RecordedCommand;
internal sealed record ResolveTextureCommand(TextureResolveRegion Resolve) : RecordedCommand;
internal sealed record BeginRenderingCommand(RenderingInfo Rendering) : RecordedCommand;
internal sealed record EndRenderingCommand : RecordedCommand;
internal sealed record SetPipelineCommand(PipelineHandle Pipeline) : RecordedCommand;
internal sealed record SetBindGroupCommand(uint GroupIndex, BindGroupHandle Group) : RecordedCommand;
internal sealed record SetBindingsCommand(uint GroupIndex, BindGroupLayoutHandle Layout, BindingWrite[] Writes) : RecordedCommand;
internal sealed record SetPushConstantsCommand(
    PipelineLayoutHandle Layout,
    ShaderStage Stages,
    uint ByteOffset,
    byte[] Data) : RecordedCommand;
internal sealed record SetViewportCommand(Viewport Viewport) : RecordedCommand;
internal sealed record SetScissorCommand(Rect Rect) : RecordedCommand;
internal sealed record SetVertexBufferCommand(uint Slot, BufferHandle Buffer, ulong Offset, uint Stride) : RecordedCommand;
internal sealed record SetIndexBufferCommand(BufferHandle Buffer, ulong Offset, IndexFormat Format) : RecordedCommand;
internal sealed record DrawCommand(uint VertexCount, uint InstanceCount, uint FirstVertex, uint FirstInstance) : RecordedCommand;
internal sealed record DrawIndexedCommand(uint IndexCount, uint InstanceCount, uint FirstIndex, int VertexOffset, uint FirstInstance) : RecordedCommand;
internal sealed record DispatchCommand(uint X, uint Y, uint Z) : RecordedCommand;
internal sealed record PushDebugGroupCommand(string Name) : RecordedCommand;
internal sealed record PopDebugGroupCommand : RecordedCommand;
