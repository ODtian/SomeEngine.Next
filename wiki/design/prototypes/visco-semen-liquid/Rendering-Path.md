# 渲染路径（Mod3）  [[visco-semen-liquid-domain]] 

走 Akinci 2012 Screen-Space Fluid Rendering，**不**走全屏 ray-marched SDF。

| Pass | 输入 | 输出 | 备注 |
|---|---|---|---|
| Depth splat | particle SOA | depth RT (max-blend) | 粒子前向遮挡轮廓 |
| Thickness splat | particle SOA | thickness RT (additive) | 用于空气边缘 alpha-mask |
| Smooth (bilateral, 5-tap × 2) | depth RT | smoothed depth RT | 粒子之间 step seams 抹平；range kernel 保护空气边缘 |
| Shade + hash noise | smoothed depth + thickness | final color | `dFdx/dFdy` → normal → Blinn-Phong；noise 用空间哈希，时间无关 |

复杂度 O(N + screen)，与粒子数无关。详见 `docs/adr/0007-mod3-akinci-screen-space-fluid.md`。

> [!warning] 2026-07-10 修订
> 上表里的 “thickness RT 用于空气边缘 alpha-mask” 与厚度模糊路线已经被原型证据否决。
> 它会把离体材料重新画成羽化圆球。以下体积守恒等值面路径覆盖该部分；双边滤波只处理深度，
> 不再扩散 thickness。

