# Text

## Current design
- 字形排版使用 HarfBuzz，栅格化使用 FreeType，生成动态 SDF 图集；常用字形在内容构建阶段预烘焙，中日韩字符动态生成。原生库通过 `LibraryImport` 调用，兼容 NativeAOT。
- 数据：`Text`（视图参数，字面文本或本地化 key 与参数）、`TextStyle`（字体、字号、颜色）、`ShapedText`（字形与位置，由排版系统写入并按版本缓存）。富文本的样式段放在缓冲中。
- `Text` 的 `Measure` 读取 `ShapedText`。
- 写入节点文本缓冲的数字与格式化重载在值不变时不分配、不推进版本。
- 当前语言是单例组件；读取它登记依赖，切换语言只重建受影响的文本。复数规则表在内容构建阶段生成。

## Open questions
- 双向文字放到二期。

## Relations
[[architecture/someui/Layout|Layout]] · [[architecture/someui/Render-Path|Render Path]]
