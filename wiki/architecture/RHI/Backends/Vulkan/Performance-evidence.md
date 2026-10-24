# Performance evidence 

The standard CPU boundary is `RenderGraph.BeginFrame` through native command-buffer close and queue
submit return. The fixed Dagor/Enlisted-derived high-watermark case has 73 passes, 200 resources and
more than 400 barriers. Current adjacent reports are:

- `artifacts/graphics-benchmarks/vulkan-graph-cpu-postfeatures-repeat.json`
- `artifacts/graphics-benchmarks/d3d12-graph-cpu-postfeatures.json`

Both satisfy the P95 `< 0.5 ms` gate; their adjacent P95 values are in the same performance tier.
