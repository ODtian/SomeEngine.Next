namespace SomeEngine.Graphics;

internal interface IPipelineFeatures
{
    bool NullDescriptors { get; }
    bool ConservativeRasterization { get; }
    bool VertexInstanceDivisorZero { get; }
    uint MaximumVertexInstanceDivisor { get; }
    bool StreamOutput { get; }
    uint StreamOutputBuffers { get; }
    uint StreamOutputBufferBytes { get; }
    uint StreamOutputStride { get; }
    uint StreamOutputBytes { get; }
    bool CountedDispatch { get; }
}

internal interface IMemoryBarrierSupport
{
    bool SplitMemoryBarriers { get; }
}
