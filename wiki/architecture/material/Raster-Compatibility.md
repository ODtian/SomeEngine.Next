# Raster Compatibility

Raster execution key 表示执行 raster 时真正需要一致的 shader entry、fixed state、specialization 与 stage ABI。

最终 bin 另外要求 command 上的 direct binding 相容。Material identity、普通 indexed 材质值和 bindless descriptor 值本身不构成 Raster execution key，也不拆 bin；只有需要重新绑定的 direct resource/view 才拆。

参见 [[Stage-Compatibility]] · [[architecture/cluster/Raster-Execution]]。
## 材质字段与深度失效

材质 scalar 上传仍处理全部材质值。是否重用已有深度由 shader 声明的依赖决定，不以 C# 类型、属性名或 opaque/masked 分类推断。

- 实际采用的主视图与 target SW/HW entry（包括 geometry、pixel、meshlet）都必须声明 `RasterMaterialInputs("evaluators")`。声明承诺：材质字段对顶点位置、varying、覆盖、深度的影响完整地经过 VertexEvaluate、DomainEvaluate、VisibilityEvaluate。
- 三个 evaluator 类型分别用 `MaterialDepthInputs("属性名,属性名")` 列出其完整材质属性依赖。空字符串显式表示无材质依赖。名称按 Material.Properties 的完整属性名解析；目前不接受成员表达式。
- 缺少声明、重复声明、找不到属性、未知 evaluator、默认 raster 或 tessellation 保持保守失效。不同顶点编码的依赖取并集，任何未声明路径会使该材质回退。
- 依赖在 stage 解析时转成属性索引。每帧使用已有属性 revision，不进行 shader 反射，也不保存另一份材质值。Shader、资源绑定或声明字段变化仍失效；只影响着色的字段仍正常编码、上传和着色。
- 同一 Texture 对象的内容变化由原 Texture.Changed 通知处理；直接纹理绑定与 indexed scalar descriptor 引用都必须使相应 caster 失效，即使 Material property revision 没变。
- 网格、变换、LOD、摄像机和灯的更新规则不由这个声明替代。

当前 ImportedScene 的 SceneMask 依赖 AlbedoMap 和 MaterialSampler；UV 来自网格，alpha cutoff 是 wrapper 常量。修改网格或 cutoff shader 仍失效。其 Roughness 不参与覆盖，但用户 shader 若把 Roughness 用作覆盖阈值，就必须把该属性列入声明。

声明是 shader 作者对完整依赖的保证。运行时不会分析 shader 源码，也不会因为某个字段叫 Roughness 就自动排除它。
