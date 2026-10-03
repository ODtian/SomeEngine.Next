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
排版保留 HarfBuzz 缓冲，直到返回的原生字形与位置范围复制完成。
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

## Glyph atlas

`GlyphAtlas` 持有 CPU 页像素、字形位置与本次提取需要复制的区域。查询以 `Font`
对象与排版输出的字形编号为键，首次查询通过 FreeType 栅格化，后续查询复用位置。

```csharp
var atlas = new GlyphAtlas(pageWidth: 1024, pageHeight: 1024,
    fontSize: 48, distanceSpread: 8);
GlyphLocation location = atlas.Get(glyph.Font, glyph.Index);
ReadOnlySpan<AtlasPage> pages = atlas.Pages;
ReadOnlySpan<AtlasDirtyRegion> added = atlas.DirtyRegions;
// 提取阶段按页与像素矩形复制数据。
atlas.ClearDirtyRegions();
```

- `FontSize` 是栅格化基准字号，单位为像素/em。字形的绘制尺寸及 `Offset` 乘以
  `TextStyleData.FontSize / atlas.FontSize`，再加排版给出的基线位置。
- `AtlasPage` 提供固定的 `Width`、`Height` 与只读 `Pixels`。像素每字节一个 SDF
  值，按行从上到下紧密排列。128 对应轮廓边界，较大值在轮廓内，较小值在轮廓外；
  像素距离为 `(value - 128) * DistanceSpread / 128`，两端按字节范围截断。
  `DistanceSpread` 支持 2–32 像素，默认 8。FreeType 读取未 hint 的轮廓，先生成抗锯齿
  位图，再用 `bsdf` 生成距离场，使相交与自交轮廓沿同一条栅格化路径处理。
- `GlyphLocation.Region` 是页编号与像素矩形；`UV0`、`UV1` 是同一矩形在该页的
  归一化边界，`Offset` 是位图左上角相对字形基线的偏移，Y 向下。空白字形的页编号
  为 -1，矩形及 UV 为零；推进仍由 `Shaper` 的结果提供。
- 页采用逐行放置，字形间留一像素边界。页容量耗尽时追加页，已有像素与 UV 保持。
  超出常规页尺寸的字形按完整 SDF 尺寸加边界创建页。
- 动态查询把新字形的矩形加入 `DirtyRegions`；`ClearDirtyRegions` 由调用方在
  提取完成后调用。加载预烘焙数据时整页加入区域，供首次提取复制。
- 同一个图集由调用方串行修改。`Pages` 与 `DirtyRegions` 的范围读到下次图集修改
  为止，页对象与已有位置随图集保留。字体可以由不同图集和排版缓冲共用，单个字体
  的 FreeType 栅格化与像素复制在同一次互斥访问内完成。

内容构建可从排版结果或字体字集取得字形编号，使用同一个图集入口预烘焙：

```csharp
atlas.Bake(font, glyphIndices);
atlas.Save(contentStream);
// 加载阶段提供来自同一组字体资产字节的字体对象。
GlyphAtlas loaded = GlyphAtlas.Load(contentStream, [font]);
```

`Save(Stream)` 写出版本化二进制内容：栅格化配置、页尺寸与放置位置、完整页像素、
字体资产的 SHA-256 摘要、字形编号、像素矩形与基线偏移。`Load(Stream, ReadOnlySpan<Font>)`
按资产摘要绑定字体，输入字体列表的顺序可变；加载后的查询复用已保存的位置，后续字形
继续在可容纳它的页内放置或追加页。两种操作均保持调用方的流打开。

API 使用错误通过 `DevelopmentCheck` 报告；文件头与版本决定二进制格式的读取方式。
`Load` 在文件边界检查栅格化配置：页宽高大于 2、字号为正数、距离范围为 2–32 像素；
非法文件配置通过 `InvalidDataException` 报告，Debug、Release 与 NativeAOT 采用相同规则。
CPU 数据沿 Game world 到 Rendering world 的既有提取路径交付，GPU 上传由 #34 承接。
相较于 SomeUI 的 `FontAtlas` 参考，这里的基准字号采用像素/em，像素保存为单通道，
字形查询消费 HarfBuzz 编号，并以追加固定页保持位置。

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
