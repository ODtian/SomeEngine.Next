# 项目构建、启动配置与渲染接入

2026-09-16。应用选择资产并持有创建后的使用对象，渲染只消费就绪数据。数据流仍是 Game world 到 Rendering world 的既有提取，不增加资产直接提交路径。

## 模板是项目自己的来源

DefaultProject 安装到空目录，复制 C#、顶层 Shader、定义和模型来源，不复制构建资产、缓存或编译输出。安装不是更新命令，遇到非空目标明确拒绝；引擎更新不覆盖已安装项目的材质、源码或默认内容。

Game.Build 使用项目源码编译 Shader，创建纹理和材质，通过项目 Importer 能力导入 glTF，然后保存 WorldAsset 和 ApplicationSettingsAsset。引擎 Shader 库只作为 include 依赖。项目之外的引用路径保留相对关系，不能退化为可能冲突的文件名。

Game.App 只使用自身输出目录的 Manifest 和构建资产。缺少构建结果时明确失败，不搜索引擎仓库或其他缓存作为回退。默认内容生成工具调用这条项目构建入口，场景工具使用同样的 Block 与材质参数 API。

## 启动与实例化

ApplicationSettingsAsset 的 ApplicationSettingsBlock 记录窗口配置、场景、Shader 资源组和各功能的明确引用。读取配置不自动实例化整张引用图；应用选择需要的资产并完成对应 Block 的转换。

WorldAsset 恢复真实 Game world。Mesh、Material 引用在原实体上解析，管线读取自己需要的 Block；同一 MeshAsset 中其他表示不会被错误地当作另一个场景身份。流送取得数据后再更新已有资源状态，渲染准备不隐藏 Asset 查询或文件 I/O。

## 生命周期与 Shader 解释

Texture 的设备关联、Mesh 的页、材质依赖及 Shader 代码分别由实际使用者保留。打开新资产、更新使用对象和旧 GPU 资源释放不是同一个时点。帧、队列和完成信号继续保护旧内容，不新增全局资产版本等待机制。

用户材质与 Shader 解释顶点和业务数据。默认项目的编码器不是引擎固定顶点布局。一个 Material 保持一个 Shader 引用，项目组合所需管线入口，不引入替换表、自动回退或根据类型名选择实现。

## 已执行的端到端验证

模板本身以及安装在引擎目录外的新项目都从来源生成 63 个资产。D3D12 的 WARP 和 RTX 3080 均完成 30 帧输出校验；独立项目使用自己的 Manifest，场景 1,024 个实例、几何页全部就绪、非空着色结果、读取失败为零。

Vulkan 的 43 项后端测试通过，但现有后端明确拒绝窗口呈现能力，本次未将其扩展成新平台功能。它不计为 Vulkan 应用多帧通过。完整命令、数量及边界见 [本次验收](Asset-Verification-2026-09-16.md)。模板使用说明见 [DefaultProject](../../../templates/DefaultProject/README.md)。
