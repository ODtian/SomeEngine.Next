# 2026-06-29 unified parcel update  [[visco-semen-liquid-domain]] 

本 demo 的权威液体实体改为 **FluidParcel**，不再区分 SurfaceParticle/VolumeParticle 两套身份。

```text
FluidParcel
  + optional SurfaceContact(faceId + bary + canonical patch/rest distance/damage)
  + reciprocal LiquidBond slots
```

- **Free FluidParcel**：无 `SurfaceContact`，邻域查询走 world-space 3D hash。
- **Contacted FluidParcel**：有 `SurfaceContact`，持久坐标是 canonical patch + barycentric，邻域候选来自 patch bucket + face/patch adjacency，再用 world distance 精确过滤。
- **VisibilityContactCapture**：free parcel 在 GPU 上读取 integer primitive id + depth + bary，并映射成持久表面接触；正式循环不经 CPU readback。
- **TransientNeighbor**：每 substep 重建，用于 repulsion/cohesion/viscosity/density-ish correction。
- **Reciprocal bond slots**：当前固定预算口径是每 parcel 最多四个互惠、generation-checked handle；它只保存 solver 内部连续性，不渲染球、棍、neck/blob。bond cap 是拓扑上限，不是临时流体邻域上限。

额外材质特性（不包含 biochemical time liquefaction）：

- **mechanical thixotropy / structure**：剪切破坏结构、低剪切恢复结构，影响 apparent viscosity。
- **gelFraction**：表达凝胶/团块异质性，增加局部粘度、bond affinity、cloudiness。
- **serum windows**：薄缘/局部清液窗口更透明；渲染层由 parcel material 字段驱动。
- **meniscus rim**：边缘/接触线高光强化，不用随机抖动。
- **bubble/void specks**：稀疏、空间 hash 固定的空洞/微泡；不做可见 ring，也不作为独立粒子。
