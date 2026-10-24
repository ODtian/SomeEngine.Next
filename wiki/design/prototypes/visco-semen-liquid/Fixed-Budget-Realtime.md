# 2026-07-10 固定预算实时扩展  [[visco-semen-liquid-domain]] 

### 统一术语

**Represented Volume（表征体积）**：parcel 当前代表的实际液体体积；它决定质量和厚度积分，
不等于 parcel 的数值支撑范围。_Avoid_：particle size、splat amount。

**Simulation Parcel（模拟 parcel）**：固定预算中的一个可变体积液体样本；parcel 是采样分辨率，
不是固定毫升数。_Avoid_：droplet、固定体积粒子。

**Numerical Support（数值支撑）**：parcel 向连续重建场贡献值的有限邻域；它只控制采样覆盖，不能
单独制造体积或可见厚度。_Avoid_：physical radius、visible radius。

**Surface Anchor（表面锚点）**：同一液体组件中持有一个有效表面接触坐标的 parcel；单个 parcel
至多一个锚点，但一个液体组件可以有多个锚点。_Avoid_：物体内部接触点。

**Internal Bond（内部互惠约束）**：连接两个当前 generation 有效 parcel 的液体内部边；它属于
液体内部拓扑，不是可见线段、胶囊或额外 strand mesh。

**Topology-Reserved Parcel（拓扑保留 parcel）**：容量压力下优先保留锚点、桥接和分支连通性的
样本；普通细节先粗化。_Avoid_：预制 U 节点、隐藏 line vertex。

### 固定预算决策

- GPU 权威状态固定为最多 1024 个可变体积 parcel；普通细节最多占 896 个槽，至少 128 个槽留给
  后续重要拓扑。总液量与 parcel 数解耦，容量满时普通体积通过保守吸收/粗化继续进入场景，禁止
  删除、拒绝或延迟液量。
- 局部 3D spatial hash 每 parcel 最多访问 16 个候选；每 parcel 最多 4 条 generation-checked
  reciprocal bond，全局最多 2048 条无向约束。不存在 all-pairs 或二次复杂度 fallback。
- 接触解析直接在 GPU 上读取半分辨率 primitive id、barycentric 和 depth；附着后每帧跟随当前表面
  三角形。正式循环不 map parcel/contact/topology buffer，验收 checkpoint 才显式读回。
- 双锚 U 是两端真实表面锚点与自由中段在重力/约束下自然下垂的连通组件；多锚分支必须在同一
  组件中实际出现 degree-3 和 degree-4 节点。约束图只用于物理与证据，不直接渲染。
- 半分辨率路径保留 depth、R16F volume-normalized thickness 和一个 3×3 depth reconstruction；
  thickness 不横向模糊，也不复制到第二张重建纹理。全分辨率只做一次 composite，并再次执行精确
  opaque-depth 遮挡。可见前表面半径还受 `0.65 × numerical support` 上限约束，防止大体积粗 parcel
  退化成巨大圆球。

当前严格场景同时保持至少 1 L、双锚 U 和多锚分支；1 L 是非生理性的容量压力案例，不是普通量的
视觉标尺。验收先完成 495 帧 checkpoint，再保持同一最终状态预热 120 帧，最后采 420 个 1080p
稳态样本，避免把验收工具同步读回造成的 GPU 冷启动计入游戏运行预算。


