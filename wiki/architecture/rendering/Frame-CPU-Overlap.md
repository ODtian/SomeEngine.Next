# Frame CPU Overlap

## Definition
GameWorld 下一轮 CPU 工作可以与已经交付给渲染侧的上一轮 CPU/GPU 工作重叠。

## Invariants
- render prepare 完成后 renderer 不再依赖该轮 GameWorld 的活跃访问。
- Game N+1 不需要等待 Render N GPU 完成。
- 需要设备反馈的决策只能作用到未来 frame。

## Relations
[[Extraction]] · [[Delayed-Feedback]]