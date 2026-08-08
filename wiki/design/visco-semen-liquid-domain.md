# Visco-Semen Liquid Domain

物理身份：精液在流变学里归入 Non-Newtonian 黏液家族。

- **shear-thinning**：粘度 η 是剪切率 γ̇ 的递减函数；慢拉时粘稠、快甩时稀薄。
  经典工程模型：Carreau / Cross / power-law。
- **viscoelastic**：既有弹性储能（spring）又有粘性耗散（dashpot）。Maxwell / Kelvin-Voigt /
  Standard Linear Solid 是经典构成模型（参见 [[https://en.wikipedia.org/wiki/Viscoelasticity]]）。
- **cohesion + adhesion + 可断裂**：黏液能黏附表面、内部拉长成丝、丝可被快拉断。
  两类断裂判据：
  - 应变判据（distance threshold）：bond 长度 > restLength × β 就断；
  - 应变率判据（strain rate threshold）：bond 的 |Δv·n̂|/length > γ̇_c 就断。
  shear-thinning 的物理直觉 = 应变率判据（快拉断）；这是简述里"粘但可断"的直接对应。

## PBD 投影映射

| 简述诉求 | PBD 约束 | 数值口径 |
|---|---|---|
| 运动学粘度（shear-thinning）| XSPH velocity smoothing，η = f(γ̇) | 每粒子局部剪切率 = ‖v_i − Σw v_j‖ / r |
| 表面张力 | cohesion 约束的 stiffness | stiffness α cohesion；rest length = r |
| 内聚（拉丝结构）| cohesion 约束 | 同上 |
| 内聚断裂 | bond 撤除 | strain-rate 阈值 γ̇_c；assert γ̇ > γ̇_c → remove bond |
| 粘附（贴表面）| adhesion 约束 vol→surface anchor | stiffness β_adh，rest = anchorLength，断阈值 = adh break |
| 摩擦（表面滑动阻力）| surface 粒子 wall-law | 速度切向分量 × μ 削减 |

## substep / iteration 数（PBD/XPBD 经验值）

XPBD (Macklin et al. 2016, Detailed Rigid Body Simulation with Extended PBD) 推荐：
substeps = 8 / frame, inner iterations = 1~4 / substep。compliance α̃ = 1 / (stiffness · dt²) ；  
高 stiffness 用低 compliance，substep 增加 → 单 substep 可降到 1 iteration。
对原型黏液，**8 substeps × 4 iterations = 32 约束投影/帧**是工业默认。

原型接受度：5~10 substeps × 4 iterations（32~40 投影/帧）足够演示黏连断裂；更多是性能代价。

> [!warning] 2026-07-10 低预算修订
> 上述数值是早期高粒子数 XPBD 方案的研究起点，不是当前实时原型预算。当前固定预算实现以
> 30 Hz、单次局部约束投影和 60 Hz 插值通过 1080p 门禁；增加迭代必须由目标硬件实测证明必要。

## 渲染路径（Mod3）

走 Akinci 2012 Screen-Space Fluid Rendering，**不**走全屏 ray-marched SDF。

| Pass | 输入 | 输出 | 备注 |
|---|---|---|---|
| Depth splat | particle SOA | depth RT (max-blend) | 粒子前向遮挡轮廓 |
| Thickness splat | particle SOA | thickness RT (additive) | 用于空气边缘 alpha-mask |
| Smooth (bilateral, 5-tap × 2) | depth RT | smoothed depth RT | 粒子之间 step seams 抹平；range kernel 保护空气边缘 |
| Shade + hash noise | smoothed depth + thickness | final color | `dFdx/dFdy` → normal → Blinn-Phong；noise 用空间哈希，时间无关 |

复杂度 O(N + screen)，与粒子数无关。详见 [[adr-0007-mod3-akinci-screen-space-fluid]]。

> [!warning] 2026-07-10 修订
> 上表里的 “thickness RT 用于空气边缘 alpha-mask” 与厚度模糊路线已经被原型证据否决。
> 它会把离体材料重新画成羽化圆球。以下体积守恒等值面路径覆盖该部分；双边滤波只处理深度，
> 不再扩散 thickness。

## 2026-07-10 体积守恒等值面修订

视觉与尺度依据采用临床/实验室资料：WHO 第六版精液实验室手册、宏观性质综述、精液高黏度
系统综述，以及 2023 年 Soft Matter 的弱凝胶流变研究。稳定结论是灰白/灰乳光、局部凝块与清液
异质、明确湿润界面；正常总量约 1.4–6.3 mL，高黏度样本可形成超过 2 cm 的丝。参考原图不进入
仓库。

渲染口径：

1. `volume` 是唯一物理尺度源；等效物理半径由体积推导，数值支撑半径不能充当可见尺寸。
2. 紧支撑厚度核做解析归一化，二维积分严格等于粒子体积。扩大支撑会降低厚度，不能凭空放大液量。
3. thickness 只做 additive accumulation，禁止各向同性模糊；双边重建与 smooth pass 只修复深度。
4. 最终轮廓由 50 µm 局部厚度等值面解析，覆盖带为 ±2%，只承担约 1–2 px 的栅格抗锯齿。
5. 已解析凝胶有最低散射项，额外不透明度再由积分厚度调制；边界 coverage 与材质 opacity 分离，
   禁止用宽 alpha 羽化伪造厚度。
6. 稀疏邻域使用面积守恒的各向异性核连接真实邻点；致密邻域缩紧支撑形成不规则凝块。贴附与自由态
   保持同一粒子身份，并允许在接触边界共同形成一个 neck。
7. 发射内允许约 ±32% 的确定性体积异质，但每次发射的总质量/总毫升数严格守恒。

尺度校验：1 mL 等体积球直径约 12.4 mm，5 mL 约 21.2 mm；当前 720p 全身构图里 10 mm 只有约
4 px。因此空中 0.45 mL 凝滴只占数个像素是正确结果，不应为了可读性放大回厘米级粒子图元。

prototype 独立 harness 固化十一类视觉证据，拒绝圆 splat 暴露、错误深度、无支撑重建、宽边缘
合成和伪造的 line/capsule/U preset；并验证 GPU visibility contact、零运行时 GPU→CPU readback、
1 L 同场存活、真实多锚内部拓扑以及 1080p 设备 timestamp 百分位预算。

## 2026-07-10 固定预算实时扩展

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


## 2026-06-29 unified parcel update

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
