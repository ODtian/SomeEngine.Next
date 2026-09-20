# substep / iteration 数（PBD/XPBD 经验值）  [[visco-semen-liquid-domain]] 

XPBD (Macklin et al. 2016, Detailed Rigid Body Simulation with Extended PBD) 推荐：
substeps = 8 / frame, inner iterations = 1~4 / substep。compliance α̃ = 1 / (stiffness · dt²) ；  
高 stiffness 用低 compliance，substep 增加 → 单 substep 可降到 1 iteration。
对原型黏液，**8 substeps × 4 iterations = 32 约束投影/帧**是工业默认。

原型接受度：5~10 substeps × 4 iterations（32~40 投影/帧）足够演示黏连断裂；更多是性能代价。

> [!warning] 2026-07-10 低预算修订
> 上述数值是早期高粒子数 XPBD 方案的研究起点，不是当前实时原型预算。当前固定预算实现以
> 30 Hz、单次局部约束投影和 60 Hz 插值通过 1080p 门禁；增加迭代必须由目标硬件实测证明必要。

