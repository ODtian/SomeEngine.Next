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
测试范围仅覆盖行为与公共API，严禁针对内部具体实现进行测试

# Rendering
渲染架构采用Game world、Rendering world分离，严禁增加其他数据通路
渲染架构不默认任何顶点属性布局，也不在运行时记录读取元数据动态多态。一切由用户自己的材质解释，管线只依赖用户shader解释

