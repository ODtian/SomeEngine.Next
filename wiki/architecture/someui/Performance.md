# Performance

## Current design
- 在 DefaultGame 统一场景平台加入标准 HUD 叠加层选项（约 1000 个元素、每帧变化的数字、一个长列表），在 1024 小球、Sponza、Bistro 三个场景中一起测量，记录重建次数、布局耗时与提取耗时。对比时该选项与其它渲染选项保持一致。
- 诊断：重建原因（视图与变化的依赖）写入诊断缓冲，默认关闭；每种视图类型对应一个 Tracy 区段。
- 初始预算，以首次实测结果校准：稳态（只有数值变化）每帧 0 分配；1000 个元素无变化时的依赖检查 ≤ 0.1 ms；100 个元素变化时重建加布局 ≤ 0.5 ms。

## Relations
[[architecture/someui/Testing|Testing]] · [[architecture/someui/Open-Questions|Open Questions]]
