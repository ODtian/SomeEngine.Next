# SomeUI Animation

## Definition
SomeUI 是引擎动画库中属性动画的使用者（[[architecture/Animation|Animation]]，ADR 0014），只增加两项与界面相关的衔接：`.Animate` 和 FLIP。界面动画使用 `Interface` 时钟，游戏暂停时照常运行。

## Invariants
- **`.Animate(channel, spec)`：** 首次调用时，在节点上加入禁用状态的 `Timeline` 和对应的 `Track<T>`。此后调用方对该字段的写入（参数或修饰）成为基准值，并在内联轨道上重定目标；呈现值由属性动画写回字段。整个过程没有结构变化，也不创建新实体。基准值的唯一写入方是调用方，呈现值的唯一写入方是动画系统。
- **FLIP：** `Flip.Enable(entity, spec)` 首次在节点上加入 `PresentationOffset`（矩阵与不透明度）及其内联轨道。`FlipSystem` 在布局之后比较前后两帧同一实体的快照（边界、变换、不透明度），算出反向的 `PresentationOffset`，再以内联轨道把它动画回到单位值。FLIP 只写 `PresentationOffset`，不写用户变换；渲染时叠加布局结果、用户变换和 `PresentationOffset`。只按同一实体匹配，不做启发式的跨实体匹配。
- 一次性流程可以直接在异步构建中使用 `await NextFrame()`，或者等待时间线完成计数。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Render-Path|Render Path]]
