# World 资产与 ECS 序列化

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

World 资产使用已有 ECS 序列化保存实体、组件、缓冲区与已注册拓扑，不再使用固定 `SceneData` 复制一套场景组件结构。ECS 的总体设计见 [[ECS-Architecture]]，通用文件能力见 [[Asset-File-IO]]。

## 集成位置

当前集成类型是 `WorldAsset : Asset<World>`。World 定义在 ECS 程序集中，不能为了写成 `World.Asset` 让 ECS 反向依赖 Assets 并制造循环。集成类型仍代表原来的 ECS World，不建立另一个 World 实现。

World 文件外层只记录资产 ID、名称、内容长度与引用等文件信息。实体、组件和拓扑内容继续使用 ECS 的格式与 codec。外层文件格式信息不能代替组件注册表。

## 显式写入、保存与加载

```csharp
// world 和 registry 由调用方建立，registry 描述需要保存的组件与拓扑。
using var asset = new WorldAsset { Name = "level" };
asset.WriteWorld(world, registry);
AssetEntry written = await asset.SaveAsync(path);

using var opened = await WorldAsset.OpenAsync(written.Path);
using World loaded = await opened.LoadAsync(registry);
```

`WriteWorld` 显式编码当前 World 内容，不写磁盘，也不持续跟踪之后的 World 修改。`SaveAsync` 写出已编码内容；`LoadAsync` 使用调用方提供的注册表建立实际 World。需要保存后续变化时，再明确调用 `WriteWorld`。

当前写入使用 ECS 的持久存档格式；读取要求对应格式。普通资产的浅复制规则不能被替换成“把对象序列化一遍来复制”：World 的这里是用户要求的显式存档操作。

当前 `WriteWorld` 会构造一份完整的编码结果，这是明确的内存成本。保存已有文件而没有重新编码 World 时，可以流式复制原内容块。读取使用范围来源与 `RangeReadStream`，不先将整个内容块另读成一个大数组。

## 引用收集

codec 调用 `DataWriter.WriteExternalReference` 时同时记录非空外部资产引用。依赖来自实际写入过程，覆盖组件、缓冲区以及拓扑 codec 的相关写入路径，再由资产公共信息规则输出。

不遍历另一套 SceneData 图来猜测依赖，也不把任意 GUID 字段自动解释为资产引用。具体 codec 必须明确表达引用语义，见 [[Asset-Identity-and-Dependencies]]。

## 示例场景与同一个 Game world

`SceneSerialization.CreateRegistry()` 提供当前示例场景使用的组件 codec：变换、MeshSource、MaterialSource 缓冲区、运动、相机和三种灯光。这是示例场景的注册集合，不是 World 资产只能包含这些组件的限制；自定义内容通过自己的注册表接入。

当前应用先从 WorldAsset 加载 Game world，再在**同一个 World、同一批实体**上解析 MeshSource 与 MaterialSource，建立实际 MeshInstance 和材质绑定。示例使用的来源组件、相机与运动数据在明确的接入步骤中消费，随后仍由现有提取流程产生 Rendering world。

这种“存档引用到应用对象”的步骤不是第三条渲染数据通路，也不需要固定的场景 DTO。完整应用流程见 [[Asset-Application-and-Rendering]]。

## 验证范围

公开行为测试覆盖实体身份、变换、材料缓冲区、Mesh 引用、运动、相机、三种灯光、去重引用、独立分支及取消加载。它不替代全部 ECS 拓扑测试，也不证明所有自定义 codec 正确。实际结果见 [[Asset-Verification-2026-09-10]]。

代码入口：[WorldAsset](../../../src/SomeEngine.Assets/WorldAsset.cs)、[ECS 序列化](../../../src/SomeEngine.ECS.Serialization/WorldSerializer.cs)、[引用写入](../../../src/SomeEngine.ECS.Serialization/DataWriter.cs)、[场景组件](../../../src/SomeEngine.Render/Components/SceneComponents.cs)、[场景 codec](../../../src/SomeEngine.Render/SceneSerialization.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Responsibilities]]、[[Asset-Sharing-and-Writes]]。
