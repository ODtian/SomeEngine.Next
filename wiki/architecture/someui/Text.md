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

`TextStyleData` 包含 `Font`、`FontSize`、`ReadOnlyMemory<Font> Fallbacks` 与 `LineHeight`。
`LineHeight` 为像素行高，零采用实际字体的行指标；正值作为每行高度，额外的行间空间
平均放在字体行框的上下两侧，字形基线与整体尺寸沿同一规则计算。
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

## Text 元素（#31）

`SomeEngine.Elements` 提供以下公开入口：

```csharp
public record struct Text(string Value) : IView, IComponent, ILayout;
public record struct TextStyle : IComponent
{
    public Font? Font;
    public float Size { get; set; }
    public Color Color { get; set; }
    public float LineHeight { get; set; }
}
public readonly record struct TextTheme(TextStyle Style) : IComponent;
public readonly struct ShapedText : IComponent
{
    public TextShape Shape { get; }
    public TextStyle Style { get; }
    public SomeEngine.Layout.Size Size { get; }
    public uint Version { get; }
}
```

`Text.Value` 是调用方写入的字符串参数。生成的 `Text(string Value)` 函数与
`Scope.View(new Text(value))` 使用同一节点身份和参数比较规则。
字体直接使用 #29 的 `SomeEngine.Typography.Font`，取代票文示例中的 `FontRef`，
使加载的字体与排版、图集共用一份字体资源。

`.Font(font)`、`.FontSize(size)`、`.TextColor(color)` 和 `.LineHeight(height)`
通过 `Scope.Write<TextStyle>` 写入调用方选择的字段。`Text.Build` 读取这些字段；
其余字段通过 `Scope.ReadUp<TextTheme>()` 取得最近祖先提供的默认样式，并登记环境依赖。
默认字号为 16 像素/em、颜色为线性白色、行高为零；字体由应用加载并提供。
显式零颜色保留 RGBA 零值；显式零行高选择字体行指标。
构建把此次解析的样式写入自身的 `TextAppearance` 数据，排版系统消费这份结果。
祖先主题变化时，继承相应字段的文本在当前重建阶段更新；显式字段继续采用调用方的值。
`ShapedText.Style` 提供此次排版使用的完整样式。

```csharp
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;

// 在调用方 Build 中提供已经加载的字体：
using (Column(5).Provide(new TextTheme(new TextStyle { Font = font, Size = 20 })).Content())
{
    Text("Title").TextColor(new Color(1, 0.8f, 0.2f));
    Text(42, "D4").FontSize(16);
}
```

## 排版与测量顺序

```csharp
var shaping = new TextShapingSystem();
systems.Add(new ViewRebuildSystem());
systems.Add(shaping);
systems.Add(new LayoutSystem(shaping));
```

同一个排版系统实例加入系统组并传入 `LayoutSystem`。布局通过共享协议
`ILayoutDataUpdate` 在每次视图测量前提供扣除 Box 内边距后的内容约束。
排版系统使用 `constraints.Max.Width` 换行，随后 `Text.Measure` 读取
`ShapedText.Size` 并按约束限制尺寸。Flex 与 Grid 的基础测量和最终测量沿同一入口，
因此文本按当前分配宽度换行，父布局使用此次测量的高度摆放后续子节点。

排版系统按文本、调用方样式与解析样式的组件版本复用字形结果。每个节点持有可复用的
各测量宽度缓冲；后续布局复用已经闲置的宽度缓冲，持续改变画布尺寸时存储按同时需要的
测量宽度数保留。画布布局结束后，以最终宽度和数据版本比较、提交 `ShapedText`。
相同文本、样式与最终约束保持 `ShapedText.Version` 及组件写版本；
相同布局矩形也保持 `LayoutRect` 的写版本。

`ShapedText` 由排版系统写入，公开读取字形、行、尺寸、完整样式和版本。
字形与行范围的读取期限为相关缓冲下一次复用或释放之前；应用在界面布局阶段结束后读取最终结果。
排版缓冲由系统持有，在节点释放后的下一次系统更新以及系统销毁时释放。
应用在相关排版与图集使用结束后释放字体。
字号、行高、字体与系统接入的使用错误通过 Core 的 `DevelopmentCheck` 报告。

## 数字与格式化写入

```csharp
public static Entity Text(int value, string format = "");
public static Entity Text<T>(T value, string format = "", IFormatProvider? provider = null)
    where T : struct, ISpanFormattable;
public static Entity DeclareText<T>(ulong callKey, T value, string format = "", IFormatProvider? provider = null)
    where T : struct, ISpanFormattable;
public static Entity SetText<T>(this Entity node, World world, T value,
    string format = "", IFormatProvider? provider = null) where T : struct, ISpanFormattable;
public static Entity SetText(this Entity node, World world, string value);
```

数字声明由同名函数拦截器提供调用位置，再通过
`Scope.View<T, TValue>(in value, create, key)` 在匹配节点后取得旧文本。
参数工厂把格式化字符与旧字符串比较，相等时复用旧字符串；变化时创建新的文本参数。
`SetText` 供调用方直接写入已存在的文本节点，遵循相同的字符比较和版本规则。
值、格式与格式提供者形成的文本保持相等时，数字写入保持托管分配为零与文本写版本。
短格式使用栈上的字符范围；较长格式使用可复用字符池。格式提供者沿用 .NET 的格式化规则，
省略格式提供者时，声明的文本在自己的 `Build` 中读取当前 `Locale`，
按该语言格式化并登记组件依赖；提供格式提供者时采用调用方指定的格式规则。
应用在声明界面之前创建语言单例；沿用纯数字写入的应用也可采用 .NET 当前文化。
`SetText` 按调用时的语言格式化并写入字符串参数。新格式首次使用时建立需要的字符池容量。

`TextTests` 经公开 API 验证 Column 高度排列、文字与主题变化、显式样式、行高、
Row 伸缩后的换行和最终版本、格式变化、长格式、数字稳态分配及开发错误。
`TextChecks` 同时用于外部消费者测试和 `just views-aot win-x64`，验证字符串、整数与
指定文化的泛型格式声明、测量、样式、版本与连续 10,000 次整数写入的零字节分配。
参考 SomeUI 的 `TextContent`、`Text` 和 `Label` 用于核对字体、字号与颜色语义；
本实现采用本页的视图参数、独立样式、排版系统写入与布局协议。

## 本地化文本（#53）

```csharp
public readonly record struct LocalizedText(string Key, ReadOnlyMemory<object?> Arguments);
public struct Locale : IComponent { public string Name; }
public readonly struct Translations : IComponent
{
    public static void Build(Stream source, Stream destination);
    public static Translations Load(Stream source);
}
public readonly record struct TextContent(string Value) : IComponent;
public static partial class TextFunctions
{
    public static Entity Text(in LocalizedText text);
    public static Entity DeclareText(ulong callKey, in LocalizedText text);
}
```

`Loc.Key("menu.start")` 创建本地化参数，`Loc.Key("items", count)` 同时提供位置参数。
`Text(in LocalizedText)` 与 `Scope.View(new Text(localized))` 创建同一种 `Text` 节点，
继续使用视图调用位置、参数比较、样式修饰和测量协议。
`Text.LocalizedValue` 保存 key 与参数；参数按 key 及每个参数的值比较，
比较同时包含会影响格式的 decimal 小数精度、日期 Kind / 时区偏移和浮点符号。
调用方在参数被视图使用期间保持其只读内存和引用参数内容，变化时提供新的参数值。

每个 World 的一个实体持有 `Locale` 与 `Translations`。应用在声明视图之前创建它：

```csharp
using var content = File.OpenRead("menu.texts");
Entity language = world.CreateEntity(new Locale { Name = "en-US" });
world.Add(language, Translations.Load(content));
// Build 中声明：
Text(Loc.Key("menu.start"));
Text(Loc.Key("items", 2));
// 在界面重建阶段之前切换语言：
world.Replace(language, new Locale { Name = "zh-CN" });
```

本地化文本在自己的 `Build` 中通过 `Scope.Read` 读取语言和翻译目录，
登记现有 ECS 组件依赖。语言切换和目录替换都沿 `ViewRebuildSystem` 更新对应文本。
普通字符串文本只读取自己的样式与主题；数字和日期声明在采用默认格式提供者时
读取语言，显式格式提供者继续控制对应节点的格式。

`Text.Value` 保留字符串声明参数，数字声明保留声明时的字符串及原始格式化值，
本地化声明以 `LocalizedValue` 表达输入。`TextContent.Value` 是该节点当前显示的
字符串，由 `Text.Build` 写入并由排版系统消费。字符串保持相等时，内容组件与
排版结果保持其版本；文本与布局继续沿现有 Game world 到 Rendering world 路径提取。
`SetText(world, string)` 把该节点的参数切换为调用方提供的普通字符串，包括空字符串。

### 内容构建与复数

内容构建阶段调用 `Translations.Build(jsonStream, contentStream)`。源 JSON 以语言名
分组，每组以 key 保存普通复合格式字符串或复数条目：

```json
{
  "en-US": {
    "menu.start": "Start",
    "items": { "argument": 0, "one": "{0} item", "other": "{0} items" },
    "summary": "{0:N2} / {1:MMMM}"
  },
  "zh-CN": {
    "menu.start": "开始",
    "items": { "argument": 0, "other": "{0} 件" },
    "summary": "{0:N2} / {1:MMMM}"
  }
}
```

构建器检查语言名与复合格式，按语言生成复数规则表，写出带版本的二进制内容。
加载阶段读取该表并解析复合格式；文本构建直接选择条目和格式化参数。
构建与加载都保持调用方的流打开。

中英文基数规则依据 [Unicode CLDR 49](https://unicode.org/cldr/charts/49/supplemental/language_plural_rules.html)：
英文在绝对值的整数部分为 1 且可见小数位为 0 时采用 `one`，其余采用 `other`；
中文统一采用 `other`。复数参数支持 C# 整数类型、`float`、`double` 与 `decimal`，
其中 `decimal` 的小数位数来自值保留的精度，所以 `1m` 与 `1.0m` 分别选择英文
`one` 与 `other`。`one` 可省略，此时使用 `other`。规则根据原始参数选择，
条目中的格式字符串控制该参数的呈现。

普通条目支持 .NET 文化名；复数条目当前接受英文和中文，构建器在内容边界
报告其它语言的规则表需求。语言目录按文化名匹配，key 按序号区分大小写。
数字与日期参数采用 `Locale.Name` 的格式规则；位置参数支持 .NET 复合格式、
对齐、转义花括号与 `IFormattable` 值。

单例、翻译目录、key 和参数的使用错误通过 Core `DevelopmentCheck` 报告，
视图上的公开 `BuildError` 提供错误信息。shipping 编译移除开发检查及其参数求值。
验收使用外部消费者的 `LocalizationTests` 和 Views NativeAOT 的公共行为场景，
覆盖语言切换、普通文本版本、复数、文化格式、参数更新和目录替换。

## 后续文本能力

文本绘制由 #34 沿既有 Game world 到 Rendering world 提取路径接入。
富文本继续按样式段扩展排版数据。

## Open questions
- 双向文字放到二期。

## Relations
[[architecture/someui/Layout|Layout]] · [[architecture/someui/Render-Path|Render Path]]
