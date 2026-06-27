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

## 渲染路径（Mod3）

走 Akinci 2012 Screen-Space Fluid Rendering，**不**走全屏 ray-marched SDF。

| Pass | 输入 | 输出 | 备注 |
|---|---|---|---|
| Depth splat | particle SOA | depth RT (max-blend) | 粒子前向遮挡轮廓 |
| Thickness splat | particle SOA | thickness RT (additive) | 用于空气边缘 alpha-mask |
| Smooth (bilateral, 5-tap × 2) | depth RT | smoothed depth RT | 粒子之间 step seams 抹平；range kernel 保护空气边缘 |
| Shade + hash noise | smoothed depth + thickness | final color | `dFdx/dFdy` → normal → Blinn-Phong；noise 用空间哈希，时间无关 |

复杂度 O(N + screen)，与粒子数无关。详见 [[adr-0007-mod3-akinci-screen-space-fluid]]。
