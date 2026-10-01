# WorldAsset、ECS 编码与语义引用

2026-09-16。WorldAsset 提供场景身份，WorldBlock 包含已有 ECS 格式编码的实体、组件、缓冲区与拓扑。它不建立固定 SceneData 或另一种 World；ECS 本身不反向依赖资产设施。

```csharp
using var asset = new WorldAsset { Name = "level" };
asset.WriteWorld(world, registry);
AssetEntry written = await asset.SaveAsync(path);
using var opened = await WorldAsset.OpenAsync(written.Path);
using World loaded = await opened.LoadAsync(registry);
```

`world`、组件与拓扑的 `registry` 由调用方提供。WriteWorld 显式编码当前状态，不保存磁盘，也不持续监听后续修改。保存写出已编码的 Block；加载完成必要读取与 ECS 解码后返回独立 World。

## 完整内容成本

编码使用现有 ECS 序列化器和 MemoryStream，产生独立的完整托管数组。显式写入仍需要容纳完整编码内容；这不是常数空间的任意增量存档承诺。未重新编码的保存可以复制原内容块。

加载明确取得存档内容并交给既有 ECS 解码器。在返回后，使用 World 不再通过 Asset 异步取得组件。分支共享不可变存档数据，读取结果使用普通 GC 生命周期，失败清理和取消遵循实际工作完成边界，不用序列化往返代替通用 Duplicate。

## 引用与同一个世界

codec 写入外部引用时收集直接资产依赖，覆盖组件、缓冲区和拓扑的相关编码。任意 GUID 不自动成为资产引用。Scene 中的几何引用是 MeshAsset，材质引用是 MaterialAsset，不绑定 Cluster 或其他管线 Block。

项目加载后在同一个 Game world、同一批实体上解析这些语义引用，按具体系统取得 Block 并创建 Mesh、Material 等使用对象。来源组件与业务状态经明确步骤接入已有提取流程，最终进入 Rendering world；不重新搭建一套资产场景图。

SceneSerialization 的示例 codec 集合不是所有用户 World 的限制。外部项目可注册自己的组件和拓扑，继续使用既有 ECS 格式及类型注册。

验证覆盖身份、变换、缓冲区、材质和 Mesh 引用、相机、运动、灯光、分支和失败行为，以及独立模板的 1,024 个实例实际绘制。见 [本次验收](Asset-Verification-2026-09-16.md)。实现：[WorldAsset](../../../src/SomeEngine.Assets/WorldAsset.cs)、[WorldBlock](../../../src/SomeEngine.Assets/WorldBlock.cs)、[ECS 编码](../../../src/SomeEngine.ECS.Serialization/)、[场景集成](../../../src/SomeEngine.Render/SceneSerialization.cs)。
