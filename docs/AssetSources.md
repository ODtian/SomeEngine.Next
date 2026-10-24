# 可读资产源文件

2026-09-16。可读源与二进制产物使用同一组强类型 Block，而不是两份业务字段模型。架构见 [Asset 与 Block](../wiki/architecture/Assets/Asset-Objects.md)，通用编码见 [BinarySerialization](BinarySerialization.md)。

## 格式与声明

当前资产文本封装为 `SomeEngine.AssetSource.2`。顶层包括 Format、TypeId、Schema、Fields、Blocks。Fields 仅保存 Id、Name、AssetType；每个 Blocks 项保存自己的 TypeId、Schema、Fields、Chunks。业务字段不能继续平铺在资产根，旧平铺格式不作为兼容读取路径。

Block 使用 `[BinarySerializable]`、`[BinaryFields]` 定义静态编码，添加 `[TextSerializable]` 后生成静态 JSON 编码。两种编码共用字段身份、顺序和嵌套类型声明；生成器不创建反射表或第二套数据对象。通用文本编码也可独立于 Asset 使用。

类型和 schema 使用已声明的持久身份，不通过 C# 类型名称查找实现。AssetText.Describe 无需业务 Block 解码即可读取公共身份与类型；可读来源本身不是已构建二进制版本。一个 ShaderAsset 的 ShaderSourceBlock 保存人工源码选择，ShaderBlock 则保存编译结果；人工来源不需要伪装成已生成全部后端代码的资产。

## 字段与外部负载

JSON 允许注释和尾逗号；缺省普通字段使用声明默认值，可空字段接受显式 null，required 字段必须提供。完整源要求共同身份字段。重复字段、未知字段、缺失必要描述和不相容类型被拒绝。非有限浮点数用字符串 NaN、Infinity、-Infinity 表示，不依赖当前区域设置。

Chunks 位于对应 Block 内，以 16 位十六进制局部键映射到相对于源文件的外部二进制路径。不同 Block 可使用相同局部键，不争用同一负载。保存源保留引用而不将像素、几何等大负载展开为 JSON 或 Base64；所有必要外部文件必须存在并满足对应 Block 的描述。仅进程内且没有持久编码的 Block 不能被静默丢弃或假装保存成功。

外部文件通过现有范围来源读入实际长度托管数组，再作为不可变数据交给 Block。读取失败不会发布不完整数据；已经取得的独立数组不因来源路径变化而失效。可读源保存与单个外部负载的关系，不等于多个独立文件在断电时的整体事务。来源复制、移动、删除及工程构建使用 AssetProject 的成组操作。

## 打开、修改与保存

可读来源由具体资产显式接入。例如 `ShaderAsset.OpenSourceAsync` 调用 `AssetText.OpenAsync`，核对资产类型，在新资产上通过 `SourceReader.ReadBlockIntoAsync` 读取所需 Block；失败时释放新资产。AssetGenerator 目前只生成共同身份及二进制文件入口，可读来源入口由具体资产实现。

`AssetText.SaveAsync` 使用同目录临时写入、刷新、身份检查和替换，具体资产的 `SaveSourceAsync` 指定需要写出的 Block。不同资产 ID 不能静默覆盖同一路径。

通用 `TextSerializer.DeserializeInto` 只应用出现的字段，可以用于局部编辑。`SourceReader.ReadBlockIntoAsync` 核对 Block 类型和 schema，再将字段及外部 chunk 读入调用方提供的 Block；出错时目标可能已经部分修改。需要失败隔离时使用新对象或 Duplicate 分支，具体资产的 `OpenSourceAsync` 应在新对象边界完成读取。

AssetText 的 Block 读写入口接受 `AssetBlock` 和静态编码能力，不要求页基类。生成的字段与集合使用普通 C# 数据，外部字节通过 SetBytes 提供；创建使用对象时遵守 [数据共享规则](../wiki/architecture/Assets/Asset-Sharing-and-Writes.md)。

字典表示为 Key/Value 对象数组，按二进制编码采用的键顺序输出；联合值表示为 Tag/Value，Tag 采用明确的联合声明。重复键、不完整条目和未知标签被拒绝。集合、可空值、组合值、字典、联合值及外部 Block 的覆盖见公开消费者测试和原生 AOT。

## 人工设置与构建记录

外部来源的完整文件名追加 `.meta`，保存来源身份、启用的 Importer、参数和元素身份映射。构建结果使用 `.import.json`；删除或损坏该记录不会丢失人工设置与身份。独立 `.shader` 已保存自身身份和选择，不再自动生成 `.shader.meta` 或同义 `.slang.meta`。

示例：[外部 Curve 扩展](../samples/SomeEngine.Curves.Assets/)、[项目 Shader 定义](../templates/DefaultProject/assets/Shaders/)、[Importer 架构](../wiki/architecture/Assets/Asset-Import-and-Storage.md)。验收状态以 [本次验证](../wiki/architecture/Assets/Asset-Verification-2026-09-16.md) 为准。
