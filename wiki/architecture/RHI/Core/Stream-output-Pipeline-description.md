# RHI-CORE-008 — Stream-output Pipeline description 

`StreamOutputState` is ordinary Graphics Pipeline state. Each `StreamOutputElement` names the exact
S# reflected output variable plus stream/component/output-slot data; explicit gaps carry no semantic
name. Buffer strides and the optional rasterized stream are caller state.

D3D12 lowers this directly to `D3D12_STREAM_OUTPUT_DESC`. Semantic storage is temporary and lives only
through native Pipeline creation. Stream output does not create another shader-interface model.
^rhi-core-008

