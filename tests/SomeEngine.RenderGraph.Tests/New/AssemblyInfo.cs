using Xunit;

// RenderGraph's integration tests create native D3D12 WARP devices. Debug-layer
// enablement is process-global, and overlapping device lifetimes also make native
// memory-pressure assertions nondeterministic, so keep this suite's device-backed
// verification serialized just like SomeEngine.Graphics.Direct3D12.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
