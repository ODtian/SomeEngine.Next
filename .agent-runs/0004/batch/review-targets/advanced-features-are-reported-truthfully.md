# Advanced features are reported truthfully

## What to review

逐项检查 DXR、mesh shader、VRS、tiled/sparse resources、sampler feedback、work graphs 的 manifest、API、docs和tests，确保本 run只记录真实 baseline/缺口，不伪造完成度。

## Pass conditions

- Mesh区分已有 `DispatchMesh` native call 与缺失的正确 AS/MS pipeline/tier closure。
- VRS区分 native calls 与缺失 tier/legal/hardware closure。
- DXR区分 state-object/build/dispatch片段与估算 prebuild、SBT/capability/hardware缺口。
- Work Graph标为旧 public-contract但 native creation未实现；sparse标为 absent；sampler feedback标为 metadata/token only。
- device snapshot 对 MeshShaderTier、VariableRateShadingTier、RayTracingTier、TiledResourcesTier、SamplerFeedbackTier、WorkGraphsTier 及对应 limits逐项明确；未查询到的事实保持 false/absent，不用默认值暗示支持。
- docs声明不高于 manifest level，unsupported usage fail-close。

## Fail conditions

- 因代码里有 enum/token/method名就写成 native support。
- 把旧 partial native片段说成“原本完全没有”，或把 out-of-scope项偷偷实现成未验证空壳。
- 没有 supporting hardware artifact却把 level升为 native-execution。

## NEEDS_GRILL

证据显示某 advanced feature属于 mandatory native baseline，或用户要求把它的 native execution纳入本 run时使用 `NEEDS_GRILL:`。
