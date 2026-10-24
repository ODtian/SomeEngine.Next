# PBD 投影映射  [[visco-semen-liquid-domain]] 

| 简述诉求 | PBD 约束 | 数值口径 |
|---|---|---|
| 运动学粘度（shear-thinning）| XSPH velocity smoothing，η = f(γ̇) | 每粒子局部剪切率 = ‖v_i − Σw v_j‖ / r |
| 表面张力 | cohesion 约束的 stiffness | stiffness α cohesion；rest length = r |
| 内聚（拉丝结构）| cohesion 约束 | 同上 |
| 内聚断裂 | bond 撤除 | strain-rate 阈值 γ̇_c；assert γ̇ > γ̇_c → remove bond |
| 粘附（贴表面）| adhesion 约束 vol→surface anchor | stiffness β_adh，rest = anchorLength，断阈值 = adh break |
| 摩擦（表面滑动阻力）| surface 粒子 wall-law | 速度切向分量 × μ 削减 |

