namespace SomeEngine.RenderGraph.Tests;

// Trace capture is process-wide. Unrelated tests must not contribute native submissions
// or consume the capture's bounded buffers while these assertions are running.
[CollectionDefinition("Render graph trace", DisableParallelization = true)]
public sealed class GraphTraceCollection;
