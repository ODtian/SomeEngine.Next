# 材质资产、强类型参数与继承

`MaterialAsset` 提供语义身份，`MaterialBlock` 保存父材质、Shader 引用以及明确的参数覆盖。材质资产家族共用 `AssetId<MaterialAsset>`。业务在 `CreateAsync<TMaterial>` 指定具体 C# 类型，并持有返回的实际对象。

Standard、Unlit 的 C# 声明、配套资产类型与共享 Slang 实现由引擎提供。生成的强类型参数入口访问 MaterialBlock，例如 `StandardMaterialParameters`。项目的具体材质声明也由 MaterialGen 生成 `IMaterial<TMaterial>.Create`；静态实现直接构造指定类型，使用类字段默认值并应用参数。

标准材质对象位于 Resources，资产类型与参数入口位于 Assets 的 Schema。`MaterialTemplates.SaveAsync(project, standardShader, unlitShader)` 保存实际白图、平法线图和两份材质模板。Standard 的 AlbedoMap 和 ARMMap 引用同一个白图，NormalMap 引用平法线图；Unlit 引用同一个白图。项目可通过 `CreateStandard`、`CreateUnlit` 取得模板内容并编辑。标量默认值由具体材料类的字段初始化表达。

```csharp
await using var loader = new AssetLoader(storage, slang, sessionDescription);
using MaterialAsset asset = await MaterialAsset.OpenAsync(loader, materialId);
using StandardMaterial material = await asset.CreateAsync<StandardMaterial>(loader);
material.Roughness = 0.25f;
```

## 局部覆盖与父级合并

每个参数记录一项局部覆盖。父资产先提供完整参数集合，当前资产逐项替换对应参数；类字段初始化提供其余值。显式零值是覆盖，显式空纹理引用是清除继承纹理。Reset 移除覆盖事实，重新遵循父级或类字段默认值。HasOverride 查询该参数的局部记录。

父级合并发生在 `CreateAsync<TMaterial>` 的明确加载边界，具体类型在生成代码中解释最终参数。父链循环在解析时报告资产数据错误。

`GetTemplateAsync(loader)` 从材质资产的父关系取得祖先模板身份。glTF Standard、Unlit 导入将模板保存为 `MaterialBlock.Parent`，参数只表达来源覆盖。实际纹理及 Shader 通过创建时的父级解析取得。

游戏通过 `ISceneMaterials` 的静态 `CreateAsync` 选择具体材质，公开启动为 `Application.RunAsync<GameMaterials>(args)`。`EngineSceneMaterials` 根据 Standard 与 Unlit 模板引用分别调用具体的 `CreateAsync<StandardMaterial>`、`CreateAsync<UnlitMaterial>`。DefaultGame 的 `SampleSceneMaterials` 使用 Imported 模板引用创建实际 `SceneMaterial`，其余材料继续使用引擎模板。

## Shader 和引用

MaterialBlock 保存 Shader 资产引用，Material 保存实际 Shader 对象引用。Shader 输入组合见 [Shader 资产](Asset-Shaders.md)。对应管线通过共同 Shader 取得自己的入口，材质解释自己的字段和曲面。

直接依赖来自 MaterialBlock 中的父材质、Shader 以及显式纹理引用。继承后实际使用的资源集合由加载结果表达。

## 创建、更新和释放

资产侧完成父级解析、覆盖合并和资源取得，然后通过静态入口返回完整具体材料。AssetLoader 在构造时接收 Slang 全局会话与一份编译设置，`GetShaderAsync` 和 `GetTextureAsync` 按资产身份共享它创建的实际对象。不同 Material 实例保存这些普通引用，MaterialId 区分业务材料实例。

业务控制 Material 的使用期，AssetLoader 控制其创建的共享 Texture 的使用期。材料释放结束该业务实例的使用；加载器释放等待资源创建结束并关闭共享 Texture。Shader 通过普通 GC 引用保存 Slang 对象与代码。

`UpdateAsync<TMaterial>` 从完整资产内容创建同类型替换字段，再通过 MaterialGen 生成的 `IMaterial<TMaterial>.Update` 直接写入已有具体实例，保留 MaterialId，并恢复 Reset 后的父级与类字段默认值。字段版本与材料 Revision 表达修改；Rendering world 保存已应用的材料 Revision。业务修改继续经过 Game world → Rendering world 的既有提取与资源完成机制。

共享 Shader 的显式刷新由 `AssetLoader.RefreshShaderAsync` 推进当前引用，材料在业务更新时取得该引用。共享 Texture 由 `RefreshTextureAsync` 保持业务对象身份并完成设备更新，见 [更新边界](Asset-Update-Boundary.md)。

2026-10-14 行为证据：MaterialCreationBehaviorTests 的消费者静态创建检查通过；MaterialFileTests、MaterialReloadTests 与 MaterialInheritanceTests 的 9 项检查通过，覆盖父级合并、局部覆盖、模板像素、实际 Shader/Texture 共享和业务实例使用。

同日 #216 集成检查：MaterialTemplateTests 与 glTF 具体创建检查通过；SharedMaterialEntryTests 从同一个 StandardMaterial 经既有提取与公开 GPU 参数绑定，验证两个入口组在业务参数修改前后的实际读回结果，覆盖 IR 和 CLI 保存的共同代码。资产 NativeAOT 原生程序完成下游自定义材料的静态创建、更新、默认值恢复与共享 Shader 使用。

实现：[MaterialAsset](../../../src/SomeEngine.Assets/Schema/MaterialAsset.cs)、[MaterialBlock](../../../src/SomeEngine.Assets/Schema/MaterialBlock.cs)、[标准材质](../../../src/SomeEngine.Resources/StandardMaterial.cs)、[材质生成器](../../../src/SomeEngine.Generators/MaterialGen.cs)。
