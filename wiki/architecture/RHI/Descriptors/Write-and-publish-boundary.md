# RHI-DSC-002 — Write and publish boundary 

`WriteDescriptor` validates table ownership, slot bounds, binding type, concrete view shape and live
resource provenance before replacing CPU-side pending content. Resource slots can use the exact
supported typed null representation. Sampler slots require a concrete Sampler because D3D12 has no
null sampler descriptor.

`PublishDescriptors(Device, nodeIndex)` atomically makes all validated pending table content available
to later command recording on that node. It is an operation, not a public version object. Failure,
cancellation, descriptor exhaustion or Device Lost cannot expose a partial update. A table index does
not change when content is written or published.

Previously recorded commands retain the native descriptor generation they captured; a later publish
does not rewrite accepted work.
^rhi-dsc-002

