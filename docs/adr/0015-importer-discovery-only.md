---
status: accepted
---

# Asset importer DI 只用于 Importer 互相发现

资产导入的扩展点只到 Importer。Asset importer DI 是 Importer 的注册表：一次来源导入中，导入上下文通过它发现本项目可用的 Importer，按来源 `.meta` 的选择实例化，并让 Importer 通过 `GetBlockAsync` 取得其它 Importer 的结果。编排者（例如 glTF 来源集成）不静态依赖任何具体 Importer；默认选择来自各包声明的导入组合，而不是写死在编排者中。

DI 中只注册 Importer、Importer 能力描述与导入组合描述。Importer 的构造函数不接收外部服务；设置经导入上下文从 `.meta` 读取；算法、编码规则与内部缓存都是 Importer 自己的实现。需要另一种算法或编码时，换一个 Importer。昂贵的外部资源（编译器会话、原生库）由使用它的 Importer 自己创建与持有，不暴露为可注入的服务。

扩展分两种方向：纵向是换管线（普通管线、Cluster 管线、用户管线），表现为启用另一组 Importer；横向是同一管线内的正交数据（蒙皮、形变、着色属性等），按 ADR 0016 划分 Block，各由自己的 Importer 产出。

当前代码中的偏离：`GltfSourceImporter` 把来源设置注册进 DI 并由场景 Importer 构造注入、生成的工厂支持任意构造参数与 `IServiceProvider`、`EnsureDefaultSelections` 写死具体 Importer 类型。这些按资产重构任务移除。

用户在 2026-10-03 确定。设计细节见 Wiki `architecture/Assets/Importer-Discovery` 与 `Importer-Sets`。
