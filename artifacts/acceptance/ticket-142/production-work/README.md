# #142 per-view work feedback

Pinned native source: 396c9f059903aed5fec78ecd3d437a40c6415368, VirtualShadowMapThrottle.usf 90–165.

The public Direct3D12 compute entry `UpdateVirtualShadowWorkThrottle` reads two previous HW populations (10,000 and 1,000), a load budget of 0.1 and EMA history weight 0.9. The submitted projection biases must be the independent native literal results 0.04 and 0.004. Red records the absent source entry; green records one passed behavior row. The production feature invokes this entry before receiver requests and carries the same writable shadow view data into sampling and quality readback.

Per-view HW/SW populations are recorded once for each accepted visible cluster/window in either phase before material/range batch expansion. The first counter word remains HW cropped page area / SW material-batch weight 64 for separate raster diagnostics; later word pairs hold HW/SW visible item counts. Compacted targets carry the source shadow index. ECS light entities and face/absolute clipmap level determine history correspondence; successful frame completion commits it.
