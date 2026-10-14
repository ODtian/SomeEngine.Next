# 项目构建、启动配置与渲染接入

2026-09-16。应用选择资产并持有创建后的使用对象，渲染只消费就绪数据。数据流仍是 Game world 到 Rendering world 的既有提取，不增加资产直接提交路径。

## 模板是项目自己的来源

DefaultProject 安装到空目录，复制 C#、顶层 Shader、定义和模型来源，不复制构建资产、缓存或编译输出。安装不是更新命令，遇到非空目标明确拒绝；引擎更新不覆盖已安装项目的材质、源码或默认内容。

游戏项目的开发组合调用引擎 `AssetProject` 编译 Shader 并导入 glTF，再保存游戏需要的 WorldAsset 和 ApplicationSettingsAsset。标准材质、实际默认图片和材料模板由引擎提供，项目准备代码选择共享 IR 与管线入口 Block。DefaultGame 持有自己的场景构建及具体材料选择。引擎 Shader 库作为明确的 include 依赖，项目之外的引用路径保留相对关系。编译边界见 [游戏项目编译与开发资产](Project-Development.md)。

Game.App 只使用自身输出目录的 Manifest 和构建资产。缺少构建结果时明确失败，不搜索引擎仓库或其他缓存作为回退。默认内容生成工具调用这条项目构建入口，场景工具使用同样的 Block 与材质参数 API。

## 启动与实例化

ApplicationSettingsAsset 的 ApplicationSettingsBlock 记录窗口配置、场景、Shader 资源组和各功能的明确引用。读取配置不自动实例化整张引用图；应用选择需要的资产并完成对应 Block 的转换。

WorldAsset 恢复真实 Game world。Mesh、Material 引用在原实体上解析，管线读取自己需要的 Block；同一 MeshAsset 中其他表示不会被错误地当作另一个场景身份。流送取得数据后再更新已有资源状态，渲染准备不隐藏 Asset 查询或文件 I/O。

公开游戏启动通过 `Application.RunAsync<TMaterials>` 采用游戏的静态 `ISceneMaterials.CreateAsync`。场景保留业务 Material、Mesh 和几何页来源。一个 AssetLoader 按显式 Slang/settings 创建并持有共享 Shader、Texture，材质与渲染配置通过对象方法取得这些依赖。Cluster 的 Shader 准备发生在渲染消费阶段。

## 生命周期与 Shader 解释

Texture 的设备关联、Mesh 的页、材质依赖及 Shader 代码分别由实际使用者保留。打开新资产、更新使用对象和旧 GPU 资源释放不是同一个时点。帧、队列和完成信号继续保护旧内容，不新增全局资产版本等待机制。

用户材质与 Shader 解释顶点和业务数据。默认项目的编码器不是引擎固定顶点布局。一个 Material 保持一个 Shader 引用，项目组合所需管线入口，不引入替换表、自动回退或根据类型名选择实现。

## 按日期记录的端到端验证

2026-10-14，#216 使用 DefaultGame 的 `sample` 准备入口构建 66 个资产，Standard 与 Unlit 分别选择共享材质 IR 和 Cluster 入口。随后经公开启动入口，以默认硬件、隐藏窗口、同步帧、关闭 TAA/VSM 和 3 帧运行完成 1024 实例的 Cluster 消费。公开 ApplicationScene 的 3 帧动作与图像取得检查同时通过。

2026-09-16 最终记录中，模板及引擎外独立项目从来源生成 63 个资产。RTX 3080 / D3D12 完成独立项目及六种场景各 30 帧输出校验；独立项目使用自身 Manifest，默认场景为 1,024 个实例，像素验证通过、读取失败为零。该轮未完成 WARP 的完整资产应用场景；WARP 的独立呈现和设备移除用例单独计数。

同一轮 Vulkan 的 43 项非呈现后端测试通过，未计为 Vulkan 窗口或应用多帧通过。完整命令、数量及平台边界见 [2026-09-16 验证](Asset-Verification-2026-09-16.md)。2026-09-29 的内容及三场景记录见 [Shader 总规范中的执行证据](../shader/Shader-Block-Residency-Refactor.md)；同日后续发现的实现和组合回归问题见 [审查记录](../../../reviews/AssetSpecReview-2026-09-29.md)。这些日期和范围分别记录，不互相替代验收。模板使用说明见 [DefaultProject](../../../templates/DefaultProject/README.md)。
