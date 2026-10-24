# RHI-CAP-007 — Mesh shaders and variable-rate shading 

`MeshShaders` reports mesh/amplification limits and stage availability. Mesh Pipeline descriptions
contain the exact reflected mesh entry, optional amplification/pixel entries, fixed-function output
state and static samplers. Unsupported amplification or mesh features fail explicitly.

`VariableRateShading` reports tier, supported rates/combiners and shading-rate image tile size.
Per-command rate and image selection are explicit commands. A shading-rate image must satisfy the
reported format, dimension, usage, sample and tile rules. No ordinary raster state is reinterpreted as
VRS support.
^rhi-cap-007

