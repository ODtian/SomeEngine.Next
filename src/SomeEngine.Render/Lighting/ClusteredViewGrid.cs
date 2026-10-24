using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Lighting;

/// <summary>The shared view parameters and cell dimensions used by spatial rendering features.</summary>
public readonly record struct ClusteredViewGrid(GraphBufferInput Parameters, uint CellCountPerView, uint ViewCount);
