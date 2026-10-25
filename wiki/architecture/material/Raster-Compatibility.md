# Raster Compatibility

Raster execution key 表示执行 raster 时真正需要一致的 shader entry、fixed state、specialization 与 stage ABI。

最终 bin 另外要求 command 上的 direct binding 相容。Material identity、普通 indexed 材质值和 bindless descriptor 值本身不构成 Raster execution key，也不拆 bin；只有需要重新绑定的 direct resource/view 才拆。Surface、Ordered 与既有透明、xray、blend 行为仍参与各自的阶段兼容性，不能用材质 ID 代替。

参见 [[Stage-Compatibility]] · [[architecture/cluster/Raster-Execution]]。

## 材质字段与深度失效

材质 scalar 上传仍处理全部材质值。是否重用已有深度由 shader 声明的依赖决定，不以 C# 类型、属性名或 opaque/masked 分类推断。

- 实际采用的主视图与 target SW/HW entry（包括 geometry、pixel、meshlet）必须满足 `RasterMaterialInputs("evaluators")` 声明。声明承诺：材质字段对顶点位置、varying、覆盖、深度的影响完整地经过 VertexEvaluate、DomainEvaluate、VisibilityEvaluate。
- 三个 evaluator 类型分别用 `MaterialDepthInputs("属性名,属性名")` 列出完整材质属性依赖。空字符串显式表示无材质依赖。名称按 Material.Properties 的完整属性名解析；不接受成员表达式。
- 缺少声明、重复声明、找不到属性、未知 evaluator、默认 raster 或 tessellation 走保守失效。不同顶点编码的依赖取并集，任一未声明路径使该材质回退。
- 依赖在 stage 解析时转成属性索引。`GetPropertyRevision` 是独立的逐属性计数；管线保存已接受的计数，并按相等性检查资源属性和声明的深度属性，不能与材料级 `Material.Revision` 作大小比较。材料 assignment 的独立 revision 快照负责定位材料变化；其发布时 PropertyCount 保留旧布局信息，供布局变化后重新解析。
- Shader 引用、完整 MaterialState、属性数量或资源绑定变化使相应路径重新解析/失效。只影响着色的字段仍正常编码、上传和着色。稳定资源与深度依赖不变时可沿用已接受的绑定计数，不因 scalar-only 变化复制另一份完整材质值；不在每帧重复 shader 反射。
- 同一 Texture 对象的原位内容更新沿 `Texture.Versions` → RenderWorld 提取的 `TextureVersions` → 管线已接受版本比较传播。直接纹理绑定与 indexed scalar descriptor 引用均须处理相应 caster 失效，即使材质属性计数没变；不恢复 `Texture.Changed` 订阅或第二套版本所有者。
- 网格、变换、LOD、摄像机和灯的更新规则不由这个声明替代。

声明是 shader 作者对完整依赖的保证。运行时不会分析 shader 源码，也不会因为某个字段叫 Roughness 就自动排除它。若覆盖逻辑读取某个字段，就必须把该字段列入声明；修改网格输入或 cutoff shader 仍按其原有规则失效。

这些是合并后的源码契约，不是深度复用率、性能或 GPU 正确性已经通过本次验收的声明。
