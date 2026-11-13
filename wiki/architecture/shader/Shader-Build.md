# Shader Build

## Definition

Shader 构建是一次显式异步 Block 行为。它可以发生在内容导入、应用启动、用户机器或运行中的资源更新边界；这些位置调用同一套 Slang compose、specialize、link、反射与 codegen 行为。

## Invariants

- `.shader` 是人工维护的 `ShaderAsset` 来源；二进制 `ShaderSourceBlock` 保存可由 Slang 重新加载的序列化模块，不依赖编辑机器路径。
- `ShaderLibrary` 按编译目标持有 Slang global session、session 与已加载模块表，在专用线程执行 Slang 调用。Shader 的全部特化变体随材质加载完成，查表直接取得已链接变体。
- `ShaderBlock.CreateAsync` 从同一来源恢复直接 Slang 对象，将所需保存代码物化为最终 GC 数组；`Shader` 通过普通引用持有这些数组，不接收页释放责任。
- 保存的整 target 代码是可选的；逐入口代码可以独立存在。缺少适用代码时，调用方可显式走 `ShaderSourceBlock` 编译路径。
- 资产冷加载按[完整输入版本和当前目标设置](../Assets/Asset-Shaders.md#构建与保存代码)选择保存代码；同一组代码、资源使用数据和 Slang 反射共同组成驻留 Shader，声明的全部特化随本次加载完成。
- 指定入口的特化保存逐入口代码和 Slang 资源使用数据；整 target 代码由共同入口编译阶段生成。
- pipeline 创建只消费已驻留 `Shader` 的直接 Slang 对象与代码，不隐式打开 Asset 或启动编译。
- 名称查找和反射发生在创建材质/pipeline 等冷路径；命令热路径只使用消费者已经派生的数据。
- 引擎 shader 在 `assets/Shaders` 烘焙一份，应用通过 GUID 与多个内容存储根引用。编译目标由 `AssetProject.ShaderTargets` 指定。
- 公共头文件使用 Slang `import`，各模块保存一份 IR。逐 shader 宏及生成常量的头文件保留 `#include`；宏配置清单随模块迁移在本页维护。
- Slang 是唯一反射来源；共享 session、模块和已链接对象承担运行时复用。

`ShaderLibrary` 的资产入口依赖 `AssetLoader` 和 Shader Block，因此 `ShaderLibrary*.cs` 由 Assets 程序集编译，命名空间仍为 `SomeEngine.Resources`。Resources 程序集持有独立的 `Shader` 使用对象；项目引用保持 Assets → Resources。库的文件读取完成后，把原生编译动作送到专用线程，线程内复用同一 session 和已加载输入。程序生成的源码和离线内容构建通过库的创建入口调用同一套编译代码。

Runtime 由进程级 `ApplicationShaders` 按目标取得唯一库，应用与场景保留消费引用，`ProcessExit` 统一关闭。显式构造的库由工具和独立调用方管理。活动输入的版本与原生对象存在同一条目中；同名版本切换的 session 刷新、实际依赖与根 IR 恢复，以及对应入口编译均在同一个 Slang 请求内完成。

`SlangShaderData` 为同一 session 的组件对象关联一个 `SessionReference`，该对象同时持有显式传入的 global 与 session 的原生引用。组件对象的普通终结完成后，`SafeHandle` 依次释放 session 和 global，使 global 的期限覆盖全部派生对象。

离线导入按并发工作量复用 global session：每次定义编译独占一个 global，编译结束归还池中并继续保留。各定义的 session 与搜索路径仍由本次编译的 `ShaderLibrary` 持有，前端操作交给其专用线程；下一个定义复用 global 中的编译器内置数据。

## 共享输入与依赖

公共输入的 `.shader` 定义将 `ShaderSourceBlock.SourceOnly` 设为 `true`，导入只保存该输入的 Slang IR。引用方通过 Slang `import` 使用输入，在 `ShaderSourceBlock.Dependencies` 中保存对应的 shader 资产身份；`SourceBuildRecord.AssetInputs` 保存本次构建使用的资产引用。模块的来源文件由它自己的构建记录持有，引用方的文件输入保留自身源码和宏配置头文件。

材质入口生成器使用的光栅与细分配置头由同一 `ShaderImports` 收集过程递归读取，其 `import` 同样进入来源块和构建记录的资产依赖。生成入口 IR 的加载据此恢复自己的真实公共输入闭包。

引擎 shader 在仓库根的 `assets/Shaders` 烘焙，目录信息保存在引擎的 `Library/AssetManifest`。应用的管线设置按定义中的 GUID 引用引擎 shader；应用自身的材质组合入口单独构建。

`EngineShaderContent.OpenFiles(contentDirectory, manifest, options)` 读取 `Library/AssetManifest/content-directories.json`，按应用、引擎的顺序建立存储。`ShaderLibrary.GetAsync(files, id)` 的依赖版本、保存代码与缓存选择由 [Shader 资产](../Assets/Asset-Shaders.md#取得与期限)维护；`SourceOnly` 输入只装入共享 session。程序生成的输入通过同一个库编译。

`tools/BuildContent` 提供 `engine <engine-root>`、`sample <project-root> <engine-root>` 和 `shader <project-root> <definition> <engine-root>` 内容构建入口。`tools/Build.ps1` 先编译 Release 内容工具，再生成内容和构建解决方案；发布复用相同编译输出。`AssetProject.BuildAllAsync` 按输入资产依赖排列构建次序，同层任务使用有界并行。

`shadow-settings <project-root> <engine-root>` 复用 `SampleContent.BuildShadowShaders` 按当前类型重写 VSM 配置资产，保留样例固定 GUID 和现有 shader 引用。该入口将 typed 配置刷新与 shader 代码构建分别提交。

定向 shader 构建与 `SampleContent.RefreshShaderAsync` 共用 `EngineShaderContent.BuildShaderAsync`。该入口沿项目定义及其本地输入读取实际 Slang import，先通过引擎项目构建所需公共输入闭包，再通过应用项目构建指定定义。消费者的构建设置包含直接资产依赖的版本；公共输入更新后，保存代码与入口 IR 在同一轮定向刷新中重新生成。

## 宏配置头文件

以下文件在调用者的编译单元中展开。清单记录实际依赖的生成数据、材质类型或宏；公共类型与纯函数由相邻的 Slang 输入资产提供。

| 宏配置头文件 | 调用者提供的配置 |
| --- | --- |
| `generated/render_instance_layout.slang` | 实例属性元数据常量、属性字数和皮肤属性位置。 |
| `raster_faces.slang` | 光栅面选择与材质属性标记宏。 |
| `cluster_instance_properties.slang` | 生成的实例属性位置和属性读取函数。 |
| `cluster_bvh_cull_common.slang` | 当前实例布局、包围体与层级剔除读取。 |
| `cluster_deform_bounds.slang` | 当前实例布局和变形包围体绑定。 |
| `cluster_common.slang` | 当前实例布局、顶点求值和材质入口绑定。 |
| `cluster_bvh_traverse_core.slang` | 当前剔除阶段、目标策略和变形包围体。 |
| `cluster_cull.slang` | 一、二阶段剔除宏及实例布局。 |
| `cluster_raster_target_filter.slang` | 当前目标策略和实例属性读取。 |
| `cluster_target_policy.slang` | 深度缓存、目标策略及当前剔除绑定。 |
| `cluster_raster_target_output.slang` | `CLUSTER_TARGET_DEPTH_CACHE` 和当前光栅目标绑定。 |
| `cluster_policy_raster_output.slang` | 当前实例布局和目标光栅输出策略。 |
| `cluster_draw_core.slang` | 顶点、可见性求值类型和光栅面配置。 |
| `cluster_shade_pipeline.slang` | 当前实例布局、材质求值、前向深度位置配置。 |
| `shade_bin_common.slang` | 当前实例布局和材质分桶绑定。 |
| `shade_bin_inputs.slang` | 继承当前实例布局生成常量，并声明可见性与层片段分桶输入。 |
| `sw_raster_core.slang` | 顶点、可见性求值、光栅面与目标策略。 |
| `cluster_tessellation_core.slang` | 顶点、曲面、可见性求值与细分变体宏。 |
| `cluster_material_entries.slang` | 材质类型别名、入口集合、顶点编码与深度/着色变体。 |
| `cluster_material_spill_entries.slang` | 当前材质求值类型别名、`CLUSTER_VERTEX_ENCODING` 顶点编码及 `CLUSTER_RASTER_BOUNDS` 包围体配置。 |
| `cluster_tessellation_entries.slang` | 材质类型别名、细分包围体与入口变体。 |
| `cluster_transparency_material.slang` | `TRANSPARENCY_MATERIAL_TYPE` 指定的用户透明材质实现。 |
| `Pipeline/cluster_material.slang` | 项目实例布局、网格/软件光栅入口配置。 |
| `cluster_bin_batches.slang` | count/scatter 调用者注入的分桶行为。 |
| `terrain_surface_cells.slang` | 调用者的 `SampleTerrainSurface` 和 `TerrainColumnCells`。 |
| `terrain_surface_transitions.slang` | 当前地形采样实现、过渡面和列单元绑定。 |
| `visibility_clear.slang` | buffer/image 可见性存储变体。 |
| `visibility_resolve.slang` | buffer/image 可见性存储变体。 |
| `vsm_page_hiz_common.slang` | 页深度层级构建/归约入口变体。 |
| `vsm_page_lists.slang` | `VSM_PAGE_LIST_READ_ONLY` 选择页列表读取或读写绑定。 |
| `vsm_sampling.slang` | 整页采样、驻留查找与预过滤查找宏。 |
| `vsm_projection.slang` | 定向光投影、驻留查找与蓝噪声配置。 |
| `vsm_material.slang` | 驻留、预过滤与蓝噪声采样宏。 |
| `vsm_receiver_sampling.slang` | 定向/局部接收面投影配置。 |
| `vsm_soft_sampling.slang` | 当前投影、驻留与软阴影采样配置。 |
| `vsm_forward_sampling.slang` | 前向材质的接收面、投影与软阴影配置。 |
| `vsm_receiver_requests.slang` | 调用者已有的光照输入及页请求绑定。 |
| `vsm_page_requests.slang` | `RENDER_LIGHTING_INPUTS_DECLARED` 选择调用者光照绑定，并继承当前 VSM 采样配置。 |

`standard_material.shader`、`unlit_material.shader` 和接口等公共输入各保存一份 IR。项目材质组合只保存自身的入口 IR，通过 `Dependencies` 引用公共输入。修改后的场景材质作为项目源码构建，继续使用公共接口输入。

## Relations

[[Slang-Authority]] · [[Shader-Composition]] · [[Shader-Block-Residency-Refactor]]
