# Text

## Typography

`SomeEngine.Typography` 是独立的文本排版能力，通过 Core 的开发检查报告 API 使用错误。
字体加载使用 FreeType 2.14.3，OpenType 排版使用 HarfBuzz 14.5.1；原生依赖由
`external/Typography/CMakeLists.txt` 固定版本与下载校验值，`LibraryImport` 生成调用代码。
构建时生成的两个原生库及许可证随消费项目复制到输出与 NativeAOT 发布目录。

公开入口：

```csharp
using Font font = Font.Load(assetBytes);
using var output = new ShapeBuffer();
var style = new TextStyleData(font, 20, fallbacks);
Shaper.Shape(text, style, wrapWidth: 240, output);
TextShape shape = output.Shape;
```

`TextStyleData` 包含 `Font`、`FontSize` 与 `ReadOnlyMemory<Font> Fallbacks`。
`Shaper.Shape` 同时提供 `ShapeBuffer` 和 `ref ShapeBuffer` 入口，调用同一排版方法。
`ShapeBuffer` 是由调用方持有的可复用对象，持有一个 HarfBuzz 缓冲和字形、行数组。
`Shape` 通过引用对象接收缓冲，使这份原生资源及其可复用存储由同一对象持有。
`TextShape` 按值返回字形和行的只读范围以及 `Size`、`LineCount`，在缓冲下次排版或
释放之前可读取。字体复制并持有加载时的资产字节，使用方在相关排版与图集数据释放后
调用 `Font.Dispose()`。同一字体可供多线程使用，各线程分别持有排版缓冲。

- `Glyph` 包含字体、字形编号 `Index`、UTF-16 索引 `TextIndex`、`Position`、`Advance`
  和 `LineIndex`。字形簇以原文索引表示；连字和组合字符保留对应的原文位置。
- `TextLine` 包含 `TextStart`、`TextLength`、`GlyphStart`、`GlyphCount`、`Width`、
  `Height` 和 `Baseline`。换行字符由行边界消费，空行保留行信息。
- 字号单位为像素，表示一个 em 的高度。位置以文本左上角为原点，Y 向下，字形位置
  表示基线位置加 HarfBuzz 的字形偏移。整体宽度采用最宽一行的推进宽度，整体高度
  为各行高度之和。每行上升部、下降部和行间距取该行实际使用字体的最大值。
- 回退字体按列表顺序查找，以完整 Unicode 文本元素选择字体。相邻使用同一字体的
  文本一起排版，保留 OpenType 连字和字距。字体列表均缺字时保留主字体的缺字字形。
- 自动换行优先采用空格与制表符后的边界，较宽的连续文本按 HarfBuzz 字形簇换行。
  行范围确定后分别排版各行，使字距与连字按当前行的文字计算，并按实际宽度修正行边界。
  一整个字形簇超过指定宽度时独占一行。空白字符保留字形推进与原文范围。
  `wrapWidth <= 0` 与正无穷表示自然宽度。`LF`、`CRLF`、`CR` 形成显式行边界。
  空字符串的尺寸、字形数与行数均为零。

测试通过公开 API 对比固定 Noto 字体子集和 HarfBuzz 14.5.1 命令行黄金数据；
参考命令与数值见 `tests/SomeEngine.Typography.Tests/Fonts/README.md`。
`just typography-aot <rid>` 沿用 ECS 的发布与启动方式完成原生程序验收。

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
