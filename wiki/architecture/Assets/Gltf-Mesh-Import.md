# glTF Mesh Import

## Definition
glTF 网格导入的顺序与算法，目标是符合 glTF 规范，并让从 Blender 导出的模型导入后与 Blender 中看到的一致。坐标约定与 glTF 相同，不做转换（ADR 0017）。Block 划分见 [[architecture/Assets/Block-Division|Block Division]]。

## Current design
来源级依赖按需拉取，不写死顺序；实际依赖链为：纹理 → 材质 → 骨架 → 网格 → 动画 → 场景组装。

单个网格分阶段处理，每个阶段由一个 Importer 负责：

| 阶段 | 产出 | 内容 |
|---|---|---|
| 1 准备 | 准备后的顶点流（仅导入进程内） | 解压（Draco、meshopt 扩展）；反量化（`KHR_mesh_quantization`）与展开稀疏 accessor；三角带、三角扇转为三角形列表；线、点图元不属于三角网格，单独跳过并报告，不让整次导入失败；无索引时生成顺序索引；缺 NORMAL 时生成平面法线（规范要求）；缺 TANGENT 时用 MikkTSpace 生成，UV 集取材质 `normalTexture.texCoord`；颜色统一为线性 RGBA，不再做 sRGB 转换；其余属性保持来源原样 |
| 2 几何 | 管线几何 Block | 合并图元为材质区段；对所有存在的顶点流（含蒙皮与形变增量）逐位比较后焊接，保留硬边与 UV 接缝；按 Deform 分区（静态、蒙皮、形变、蒙皮加形变），分区后才建 cluster；LOD 简化时锁住 UV 接缝、材质边界与法线不连续处，蒙皮分区把权重计入误差，形变分区计入增量 |
| 3 顶点属性 | 管线顶点属性 Block | 全部法线、切线、UV 集、颜色与自定义属性，按该管线 Vertex 实现的编码，记录兼容性标识 |
| 4 蒙皮 | 中立蒙皮绑定 + 管线蒙皮影响 | 逆绑定矩阵与关节到骨架的映射；每个顶点的全部 `JOINTS_n`/`WEIGHTS_n`，数量与数值照搬来源；规范要求权重和为 1，只检查并报告，不修改 |
| 5 形变 | 中立形变目标集合 + 管线形变增量 | 名称取 `mesh.extras.targetNames`，默认权重取 `mesh.weights`；逐目标增量 |

## Invariants
- 引擎不设阈值：骨骼影响数量、UV 集、颜色集照搬来源；如何使用由着色器决定。
- 不重算来源已有的法线；不跨不同法线、UV 或任何顶点流焊接。
- 切线生成必须使用 MikkTSpace，与 Blender 烘焙法线贴图的方式一致。
- 蒙皮网格所在节点的变换按规范在蒙皮时忽略；镜像变换由运行时按行列式处理绕序。

## Evidence
Blender 导出行为与对应要求：

| Blender 导出 | 导入要求 | 当前代码 |
|---|---|---|
| 逐角法线（平滑组、自定义拆分法线已按角拆分顶点） | 不重算，不跨法线焊接 | 符合（按属性逐位去重） |
| 默认不导出切线，法线贴图按 MikkTSpace 烘焙 | MikkTSpace 生成 | 不符合：`ClusterBuilder.GenerateTangentAttribute` 为逐三角形累加；仅在设置开启时生成；固定使用 `TEXCOORD_0` |
| 无法线图元（规范允许） | 生成平面法线 | 未实现；生成切线时要求 NORMAL 存在 |
| 顶点色已为线性值 | 按线性处理 | 顶点属性 Block 迁移前，标准编码暂时跳过颜色并报告警告 |
| UV 的 V 已翻转为左上角原点 | 原样使用 | 符合 |
| 默认 4 个骨骼影响，"包含全部影响"时多出 `JOINTS_1`/`WEIGHTS_1` | 照搬全部 | 未实现蒙皮处理 |
| 形状键导出为形变目标 | 支持 | 未实现 |
| 多个材质槽对应多个图元 | 合并为材质区段 | 符合 |
| 可选导出松散的边与点 | 跳过该图元并报告 | 不符合：`ClusterBuilder` 遇到非三角形图元使整次导入失败 |
| 多余的 UV 集 | 照搬 | 顶点属性 Block 迁移前，标准编码暂时跳过 `TEXCOORD_0` 以外的 UV 并报告警告；全部来源属性参与顶点焊接 |

## Relations
[[architecture/Assets/Block-Division|Block Division]] · [[architecture/Assets/Asset-Meshes|Mesh]] · [[architecture/ECS/Coordinate-System|Coordinate System]] · ADR 0016 · ADR 0017
