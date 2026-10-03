# Render Path

## Definition
界面渲染数据沿 Game world 到 Rendering world 的既有提取路径流动，并按更新频率区分持久部分与随变化更新的部分。

## Invariants
- 视觉都是材质加用户 shader。`Surface`（填充、圆角、描边，由 `Background`/`Corner`/`Border` 修饰写在参与布局的节点上）、`Image`、`Glyphs` 使用引擎自带的普通材质；渲染管线不识别控件，也不预设顶点布局。
- 界面 pass 的拓扑只在增删 `Canvas` 或改变渲染目标时变化，放在持久 RenderGraph 中通过显式修改维护。
- 实例数据按 ECS 写版本以块为单位增量提取，更新 Rendering world 中常驻的实例缓冲；静态界面不重新提取。
- 节点的最终呈现为布局结果、用户变换与 `PresentationOffset` 的叠加。
- 绘制顺序取树序；裁剪矩形位于实例数据中。
- 世界空间界面与屏幕界面使用同一套元素，`Canvas` 指向世界中的实体，由其变换定位。
- 不新增任何数据通路。

## Relations
[[architecture/rendering/Extraction|Extraction]] · [[architecture/Render-Graph|Render Graph]] · [[architecture/someui/Layout|Layout]]
