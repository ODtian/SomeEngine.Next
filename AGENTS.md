项目使用C#
# 代码风格
此引擎是给人类使用的，请注意代码风格不要写无法辨识的代码
数据优先以是什么数据命名，行为优先以是什么行为命名，不要用行为命名数据，用数据命名行为
禁用词：Authoring Lease Cursor Plan Slot Owner Lease Runtime Publication Admission Canonical Exact Stable Physical Logical Prepared Pending Snapshot Fingerprint Module Program Sink Contract Abi Invocation，原因是含义空洞，不是具体数据也不是具体行为
Debug、优化时，先问是否是架构造成的，不要在错误的架构下打补丁，能重构则尽早重构
所有权、数据、状态是否有且仅有一个主要的持有者，是否进行了多余的维护

# Validation
代码不要过度检验，先问值、状态是否已经在内部形成了不变量。
如果需要检验，优先调用引擎统一的validation层（不止graphic）使其可以在运行时关闭。只有真的影响功能，才在运行时路径检验

临时运行、临时脚本、临时结果、临时探针，使用完成后立即删除，不要把临时内容保留在工作区内或者临时目录/

# Testing
测试按 [行为检查规则](wiki/harness/Behavior-Checks.md) 编写和评审；默认测试命令执行该页规定的自动检查。

渲染性能优化必须使用 DefaultGame 的统一场景平台，同时测试 1024 小球、Sponza、Amazon Bistro。使用 tools/Measure-Scenes.ps1 执行一次不超过 60 秒的多场景反馈；内容构建另计。未收敛、失败、缺失场景的报告不能视为性能通过。对比必须保持分辨率、场景行为、渲染选项、JIT 配置和采样方式一致；带 trace 的定位结果与不带 trace 的性能结果分开记录。

# Rendering
渲染架构采用Game world、Rendering world分离，严禁增加其他数据通路
渲染架构不默认任何顶点属性布局，也不在运行时记录读取元数据动态多态。一切由用户自己的材质解释，管线只依赖用户shader解释


RG 采用持久图，稳定拓扑和资源通过显式修改维护。按更新频率拆分：接近每帧刷新且重建更便宜的工作使用即时子图或当帧重建，不为这类工作强行维护增量拓扑，也不能因此每帧重建主体图。

## Agent skills

### Issue tracker

需求规格与任务票使用 GitHub Issues：`ODtian/SomeEngine.Next`。执行相关技能前读取 `docs/agents/issue-tracker.md`。

### Triage labels

任务角色与标签名称见 `docs/agents/triage-labels.md`。

### Domain docs

领域文档采用根级 `GLOSSARY.md`、`docs/adr/` 与现有 Wiki。探索代码前读取 `docs/agents/domain.md`。当前行为以源码核对结果为准，已确认的需求和设计目标用于规划变更；Wiki/ADR 的引用范围按该文件中的筛选规则确定。
