# 液体样例的批量更新与渲染提取

本页属于现行实现与公共 API 使用说明。源码入口为 [LiquidFlow](../../samples/SomeEngine.Liquid/LiquidFlow.cs)、[LiquidExtraction](../../samples/SomeEngine.Liquid/LiquidExtraction.cs) 与 [LiquidDemo](../../samples/SomeEngine.Liquid/LiquidDemo.cs)，行为入口为 [批量步进](../../tests/SomeEngine.Liquid.Tests/LiquidBatchTests.cs)、[渲染提取](../../tests/SomeEngine.Liquid.Tests/LiquidExtractionTests.cs) 与 [曲线几何](../../tests/SomeEngine.Liquid.Tests/LiquidGeometryTests.cs)。

`LiquidFlow.Step(float)` 同步更新一个流。`LiquidFlow.Step(ReadOnlyMemory<LiquidFlow>, float)` 同步更新一组独立的流，两者共同使用当前 `JobSystem.Current` 的范围执行能力，返回时全部流已完成。批量输入中的每项对应一个独立对象；每个对象内部依次执行表面合并、动量交换、点更新、吸收、再次合并、移除与发射，时间按原有步长推进。

以下是批量调用的使用例子：

```csharp
LiquidFlow[] flows = [firstFlow, secondFlow];
LiquidFlow.Step(flows, LiquidFlow.StepSeconds);
```

游戏实体通过 `LiquidBody` 持有流、视图和修订号。更新完成后替换该组件，使普通变化追踪将结果交给 `RenderExtractionSystems`。`LiquidExtraction` 为每个源实体复用两个 `LiquidFrame`，范围收集工作写入各自实体的收集缓冲，统一提取边界随后为每个源实体更新 `LiquidRenderData` 并交换缓冲。曲线、体积、材质、障碍物和相机数据经同一个 Game world 到 Rendering world 的提取入口交付。

调用方通过提取组的 `RequireMirror(source)` 取得对应渲染实体，再从 Rendering world 读取其 `LiquidRenderData`。样例的单流颜色特性使用 `LiquidExtraction.RenderEntity` 取得当前首个流的渲染实体。`LiquidDemo` 通过 `ApplicationScene.InstallGeometry` 注册提取，通过普通应用更新与颜色特性入口完成步进和呈现。
